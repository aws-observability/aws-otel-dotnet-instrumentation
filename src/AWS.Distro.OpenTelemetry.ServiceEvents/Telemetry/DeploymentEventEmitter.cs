// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// Emits the <c>aws.service_events.deployment_event</c> signal at process start
/// and periodically re-emits it for long-running services.
/// </summary>
/// <remarks>
/// <para>
/// Deployment context is read from <c>OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_*</c>
/// and <c>OTEL_AWS_SERVICE_EVENTS_GIT_*</c> env vars per spec §6.
/// </para>
/// <para>
/// Re-emission cadence (24 hours) matches the Python SDK's
/// <c>FunctionCallCollector._deployment_event_interval_seconds</c>.
/// Backend ingestion picks up the latest record so long-running services
/// stay queryable even if the original startup record aged out.
/// </para>
/// </remarks>
internal sealed class DeploymentEventEmitter : IDisposable
{
    private static readonly TimeSpan ReEmissionInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// What the dispose path would wait for an in-flight re-emission if it had the whole shutdown
    /// window to itself. Clamped by the shared budget in practice.
    /// </summary>
    private static readonly TimeSpan TimerDrainWait = TimeSpan.FromMilliseconds(250);

    private readonly ServiceEventsOtlpEmitter emitter;
    private readonly DeploymentContext context;
    private Timer? timer;
    private bool disposed;

    private DeploymentEventEmitter(ServiceEventsOtlpEmitter emitter, DeploymentContext context)
    {
        this.emitter = emitter;
        this.context = context;
    }

    /// <summary>
    /// Take deployment context from the resolved config, emit a <c>startup</c> event once
    /// immediately, and schedule periodic (<c>periodic</c>) re-emissions every 24 hours.
    /// </summary>
    /// <param name="emitter">The OTLP emitter to feed.</param>
    /// <param name="config">Resolved config supplying the deployment/VCS provenance.</param>
    /// <returns>An owner that schedules the re-emission timer.</returns>
    public static DeploymentEventEmitter StartAndEmit(ServiceEventsOtlpEmitter emitter, ServiceEventsConfig config)
    {
        var context = DeploymentContext.FromConfig(config);
        var owner = new DeploymentEventEmitter(emitter, context);
        owner.Emit("startup");
        owner.timer = new Timer(_ => owner.Emit("periodic"), state: null, ReEmissionInterval, ReEmissionInterval);
        return owner;
    }

    /// <summary>Stop the re-emission timer and emit a final <c>shutdown</c> event.</summary>
    public void Dispose() => this.Dispose(ShutdownBudget.FromNow(ShutdownBudget.Default));

    /// <summary>
    /// Stop the re-emission timer and emit a final <c>shutdown</c> event, drawing any wait from a
    /// shared shutdown deadline. Idempotent.
    /// </summary>
    /// <param name="budget">
    /// Deadline shared with the other disposables torn down in the same pass.
    /// </param>
    internal void Dispose(ShutdownBudget budget)
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        try
        {
            // Wait for a periodic re-emission that is already running, so the shutdown emit below
            // cannot interleave with it and the caller does not dispose the OTLP providers while an
            // emit is still in flight. Same reasoning as CollectorBase.Dispose; far less likely to
            // matter here because the cadence is 24 hours, but the pattern should not differ.
            // Drawn from the shared budget so it cannot consume the window the exporter flush needs.
            var pending = this.timer;
            this.timer = null;

            if (pending is not null)
            {
                // Not a `using`, for the same reason as CollectorBase.Dispose: the runtime will
                // signal this handle when the in-flight callback returns, so disposing it while that
                // is still pending crashes the process from a thread-pool thread. Disposed only when
                // the wait actually completed, or when no signal is coming at all.
                var drained = new ManualResetEvent(false);
                if (!pending.Dispose(drained) || drained.WaitOne(budget.Clamp(TimerDrainWait)))
                {
                    drained.Dispose();
                }
            }
        }
        catch
        {
            // Timer dispose never throws but guard anyway — ServiceEvents never
            // crashes the host on shutdown.
        }

        // Final shutdown emit so the backend records graceful termination.
        this.Emit("shutdown");
    }

    private void Emit(string trigger)
    {
        try
        {
            this.emitter.EmitDeploymentEvent(this.context.ToEvent(trigger));
        }
        catch
        {
            // Telemetry must never crash the host. Failed emissions are dropped.
        }
    }

    /// <summary>Immutable deployment context, resolved once at startup.</summary>
    private sealed record DeploymentContext(
        string? GitCommitSha,
        string? GitRepoUrl,
        string? DeploymentId,
        string? DeploymentUrl,
        string? DeploymentTimestamp)
    {
        /// <summary>
        /// Project the deployment provenance out of the resolved config. Sourced from config rather
        /// than read from the environment directly so there is one place that decides what an env
        /// var means, and so a caller can construct an emitter without mutating process state.
        /// </summary>
        /// <param name="config">The resolved ServiceEvents config.</param>
        /// <returns>The deployment context for this process.</returns>
        public static DeploymentContext FromConfig(ServiceEventsConfig config) => new(
            GitCommitSha: NullIfEmpty(config.GitCommitSha),
            GitRepoUrl: NullIfEmpty(config.GitRepoUrl),
            DeploymentId: NullIfEmpty(config.DeploymentId),
            DeploymentUrl: NullIfEmpty(config.DeploymentUrl),
            DeploymentTimestamp: NullIfEmpty(config.DeploymentTimestamp));

        public DeploymentEvent ToEvent(string trigger) => new(
            Trigger: trigger,
            GitCommitSha: this.GitCommitSha,
            GitRepoUrl: this.GitRepoUrl,
            DeploymentId: this.DeploymentId,
            DeploymentUrl: this.DeploymentUrl,
            DeploymentTimestamp: this.DeploymentTimestamp);

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrEmpty(value) ? null : value;
    }
}

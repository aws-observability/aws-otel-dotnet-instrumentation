// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Models;

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
    /// Build deployment context from the current environment, emit a
    /// <c>startup</c> event once immediately, and schedule periodic
    /// (<c>periodic</c>) re-emissions every 24 hours.
    /// </summary>
    /// <param name="emitter">The OTLP emitter to feed.</param>
    /// <returns>An owner that schedules the re-emission timer.</returns>
    public static DeploymentEventEmitter StartAndEmit(ServiceEventsOtlpEmitter emitter)
    {
        var context = DeploymentContext.FromEnvironment();
        var owner = new DeploymentEventEmitter(emitter, context);
        owner.Emit("startup");
        owner.timer = new Timer(_ => owner.Emit("periodic"), state: null, ReEmissionInterval, ReEmissionInterval);
        return owner;
    }

    /// <summary>Stop the re-emission timer and emit a final <c>shutdown</c> event.</summary>
    public void Dispose()
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
            // Bounded so shutdown can never hang on it.
            var pending = this.timer;
            this.timer = null;

            if (pending is not null)
            {
                using var drained = new ManualResetEvent(false);
                if (pending.Dispose(drained))
                {
                    drained.WaitOne(TimeSpan.FromSeconds(2));
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

    /// <summary>Immutable deployment context read once from the environment.</summary>
    private sealed record DeploymentContext(
        string? GitCommitSha,
        string? GitRepoUrl,
        string? DeploymentId,
        string? DeploymentUrl,
        string? DeploymentTimestamp)
    {
        public static DeploymentContext FromEnvironment() => new(
            GitCommitSha: NullIfEmpty(Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_GIT_COMMIT_SHA")),
            GitRepoUrl: NullIfEmpty(Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_GIT_REPO_URL")),
            DeploymentId: NullIfEmpty(Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_ID")),
            DeploymentUrl: NullIfEmpty(Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_URL")),
            DeploymentTimestamp: NullIfEmpty(Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_TIMESTAMP")));

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

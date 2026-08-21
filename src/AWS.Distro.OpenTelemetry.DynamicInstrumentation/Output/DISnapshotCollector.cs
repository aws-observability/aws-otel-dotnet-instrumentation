// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Background daemon thread that drains the DIDataStore queue every 10ms
/// and passes captures to the OTLP emitter.
/// </summary>
internal sealed class DISnapshotCollector : IDisposable
{
    private const int DrainIntervalMs = 10;

    private readonly IDISnapshotEmitter emitter;
    private readonly CancellationToken ct;
    private Thread? thread;

    public DISnapshotCollector(IDISnapshotEmitter emitter, CancellationToken ct)
    {
        this.emitter = emitter;
        this.ct = ct;
    }

    public void Start()
    {
        this.thread = new Thread(this.DrainLoop) { IsBackground = true, Name = "DI-SnapshotCollector" };
        this.thread.Start();
    }

    public void Dispose()
    {
        // The drain loop exits when the (externally-managed) CancellationToken is cancelled, but that is
        // asynchronous — the thread may still be inside its final DrainOnce, calling emitter.Emit. Join it
        // (bounded) so the caller can safely dispose the emitter afterward without Emit hitting a disposed
        // LoggerFactory. The bound guards against a wedged emitter; the final drain is normally sub-ms.
        this.thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void DrainLoop()
    {
        while (!this.ct.IsCancellationRequested)
        {
            try
            {
                this.DrainOnce();
                Task.Delay(DrainIntervalMs, this.ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Survive unexpected errors — keep draining.
            }
        }

        // Best-effort final drain on shutdown.
        try
        {
            this.DrainOnce();
        }
        catch
        {
            // Shutdown drain is best-effort.
        }
    }

    private void DrainOnce()
    {
        // Queue growth is bounded in practice: HitState rate-limits captures to 5/sec per instrumentation
        // (enqueue side), and the snapshot exporter's 10s timeout (DISnapshotOtlpEmitter.Create) makes Emit
        // fail fast on a wedged endpoint rather than block the drain thread (drain side).
        foreach (var capture in DIDataStore.Drain())
        {
            try
            {
                this.emitter.Emit(capture);
            }
            catch
            {
                // Per-capture errors don't crash the collector.
            }
        }
    }
}

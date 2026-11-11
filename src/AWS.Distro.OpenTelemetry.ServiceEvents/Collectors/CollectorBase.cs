// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Base class for periodic telemetry collectors. Ports the flush-loop pattern
/// from the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/collectors/base_collector.py"><c>base_collector.py</c></see>
/// and the Java distro's
/// <see href="https://github.com/aws-observability/aws-otel-java-instrumentation/blob/main/instrumentation/serviceevents/src/main/java/software/amazon/opentelemetry/javaagent/instrumentation/serviceevents/collectors/BaseCollector.java"><c>BaseCollector</c></see>:
/// a background timer calls <see cref="Collect" /> every
/// flush interval, and a final <see cref="Collect" /> runs on dispose to drain
/// pending data.
/// </summary>
/// <remarks>
/// <para>
/// .NET mapping: Python's daemon thread + interruptible <c>Event.wait()</c>
/// becomes a <see cref="System.Threading.Timer" />; the final-flush-on-shutdown
/// becomes <see cref="Dispose()" />. Overlapping ticks are skipped (a slow
/// <see cref="Collect" /> never runs concurrently with itself), and exceptions
/// thrown by <see cref="Collect" /> are swallowed so a telemetry failure never
/// crashes the host.
/// </para>
/// </remarks>
internal abstract class CollectorBase : IDisposable
{
    /// <summary>
    /// What the dispose path would wait for an in-flight timer callback if it had the whole
    /// shutdown window to itself. Clamped by the shared budget in practice.
    /// </summary>
    private static readonly TimeSpan TimerDrainWait = TimeSpan.FromMilliseconds(500);

    private readonly int flushIntervalMs;
    private readonly object stateLock = new();

    private Timer? timer;
    private int collecting; // 0 = idle, 1 = a Collect() is in progress (Interlocked guard)
    private bool started;
    private bool disposed;

    // Non-null only while Dispose is running, so periodic collection is never subject to a
    // shutdown deadline that does not apply to it.
    private ShutdownBudget? shutdownBudget;

    /// <summary>Initializes a new instance of the <see cref="CollectorBase"/> class.</summary>
    /// <param name="flushIntervalMs">How often <see cref="Collect" /> runs, in milliseconds.</param>
    /// <param name="name">Collector name, for diagnostics.</param>
    protected CollectorBase(int flushIntervalMs, string name)
    {
        this.flushIntervalMs = flushIntervalMs > 0 ? flushIntervalMs : 30_000;
        this.Name = name;
    }

    /// <summary>Gets collector name, used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Start the periodic flush timer. Idempotent.</summary>
    public void Start()
    {
        lock (this.stateLock)
        {
            if (this.started || this.disposed)
            {
                return;
            }

            this.started = true;

            // First tick after one interval; subsequent ticks every interval.
            this.timer = new Timer(
                _ => this.RunCollectSafely(),
                state: null,
                dueTime: this.flushIntervalMs,
                period: this.flushIntervalMs);
        }
    }

    /// <summary>Stop the timer and perform a final drain. Idempotent.</summary>
    public void Dispose() => this.Dispose(ShutdownBudget.FromNow(ShutdownBudget.Default));

    /// <summary>
    /// Stop the timer and perform a final drain, with every wait drawn from a shared shutdown
    /// deadline. Idempotent.
    /// </summary>
    /// <param name="budget">
    /// Deadline shared with the other disposables torn down in the same pass, so their bounded waits
    /// cannot add up past the process-exit window.
    /// </param>
    internal void Dispose(ShutdownBudget budget)
    {
        Timer? pending;

        lock (this.stateLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            pending = this.timer;
            this.timer = null;
        }

        this.shutdownBudget = budget;

        // Wait for an in-flight tick to finish before the final drain. The parameterless
        // Timer.Dispose() returns immediately, which used to produce two problems: the `collecting`
        // guard turned the final RunCollectSafely() below into a no-op (losing the last window), and
        // the caller went on to dispose the OTLP providers underneath a Collect() that was still
        // emitting. Timer.Dispose(WaitHandle) signals only once all callbacks have returned.
        if (pending is not null)
        {
            // Deliberately not a `using`. Timer.Dispose(WaitHandle) is a promise from the runtime
            // that it will signal this handle once the in-flight callback returns, and it keeps a
            // strong reference in order to do so. Disposing the handle while that promise is
            // outstanding — which is exactly what the timeout path below does — turns the eventual
            // signal into an ObjectDisposedException on a thread-pool thread. That is unhandled, and
            // it takes the whole process down during shutdown. So the handle is disposed only on the
            // paths where the runtime is provably done with it; on timeout it is left to the GC,
            // leaking one event handle once per process shutdown, which is the cheaper outcome.
            var timerDrained = new ManualResetEvent(false);
            if (pending.Dispose(timerDrained))
            {
                // Drawn from the shared budget rather than a fixed cap of its own: a callback that
                // takes the whole window would otherwise leave nothing for the remaining teardown
                // steps or the exporter flush. On timeout we skip the final flush rather than race
                // the still-running tick.
                if (!timerDrained.WaitOne(budget.Clamp(TimerDrainWait)))
                {
                    return;
                }

                timerDrained.Dispose();
            }
            else
            {
                // The timer was already disposed, so no signal is coming and the handle is ours.
                timerDrained.Dispose();
            }
        }

        // Final flush outside the lock so we don't hold it during emission.
        this.RunCollectSafely();
    }

    /// <summary>
    /// The lesser of <paramref name="requested" /> and whatever remains of the shutdown budget.
    /// Returns <paramref name="requested" /> unchanged during normal operation, when no shutdown is
    /// in progress — periodic collection is not racing a process-exit deadline.
    /// </summary>
    /// <param name="requested">The wait the caller would perform if unconstrained.</param>
    /// <returns>How long the caller may actually wait.</returns>
    protected TimeSpan ClampToShutdownBudget(TimeSpan requested) =>
        this.shutdownBudget?.Clamp(requested) ?? requested;

    /// <summary>
    /// Collect and emit the window's telemetry. Called periodically by the
    /// timer and once more on <see cref="Dispose()" />. Implementations must be
    /// safe to call from a background thread.
    /// </summary>
    protected abstract void Collect();

    /// <summary>
    /// Run <see cref="Collect" /> with a non-reentrancy guard and exception
    /// isolation. A tick that arrives while a previous <see cref="Collect" />
    /// is still running is skipped rather than queued.
    /// </summary>
    private void RunCollectSafely()
    {
        // Skip if a Collect() is already in flight.
        if (Interlocked.CompareExchange(ref this.collecting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            this.Collect();
        }
        catch (Exception ex)
        {
            // Telemetry must never crash the host. Drop and continue — but say so, because this
            // silently loses a whole flush window.
            ServiceEventsEventSource.Log.CollectFailed(this.Name, ex);
        }
        finally
        {
            Interlocked.Exchange(ref this.collecting, 0);
        }
    }
}

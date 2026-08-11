// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Base class for periodic telemetry collectors. Ports the flush-loop pattern
/// from the Python SDK's <c>collectors/base_collector.py</c> and Java's
/// <c>BaseCollector</c>: a background timer calls <see cref="Collect" /> every
/// flush interval, and a final <see cref="Collect" /> runs on dispose to drain
/// pending data.
/// </summary>
/// <remarks>
/// <para>
/// .NET mapping: Python's daemon thread + interruptible <c>Event.wait()</c>
/// becomes a <see cref="System.Threading.Timer" />; the final-flush-on-shutdown
/// becomes <see cref="Dispose" />. Overlapping ticks are skipped (a slow
/// <see cref="Collect" /> never runs concurrently with itself), and exceptions
/// thrown by <see cref="Collect" /> are swallowed so a telemetry failure never
/// crashes the host.
/// </para>
/// </remarks>
internal abstract class CollectorBase : IDisposable
{
    private readonly int flushIntervalMs;
    private readonly object stateLock = new();

    private Timer? timer;
    private int collecting; // 0 = idle, 1 = a Collect() is in progress (Interlocked guard)
    private bool started;
    private bool disposed;

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
    public void Dispose()
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

        // Wait for an in-flight tick to finish before the final drain. The parameterless
        // Timer.Dispose() returns immediately, which used to produce two problems: the `collecting`
        // guard turned the final RunCollectSafely() below into a no-op (losing the last window), and
        // the caller went on to dispose the OTLP providers underneath a Collect() that was still
        // emitting. Timer.Dispose(WaitHandle) signals only once all callbacks have returned.
        if (pending is not null)
        {
            using var timerDrained = new ManualResetEvent(false);
            if (pending.Dispose(timerDrained))
            {
                // Bounded so a wedged Collect() cannot hang process shutdown; ProcessExit gives us
                // only a couple of seconds in total. On timeout we skip the final flush rather than
                // race the still-running tick.
                if (!timerDrained.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    return;
                }
            }
        }

        // Final flush outside the lock so we don't hold it during emission.
        this.RunCollectSafely();
    }

    /// <summary>
    /// Collect and emit the window's telemetry. Called periodically by the
    /// timer and once more on <see cref="Dispose" />. Implementations must be
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
        catch
        {
            // Telemetry must never crash the host. Drop and continue.
        }
        finally
        {
            Interlocked.Exchange(ref this.collecting, 0);
        }
    }
}

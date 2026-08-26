// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Rate-limiting and per-error deduplication for IncidentSnapshot, ported from the
/// Java SDK's <c>IncidentRateLimiter</c>. Uses a lock-free <b>tumbling window</b>:
/// all per-window state lives in a <see cref="Window" /> that is atomically
/// swapped (via <see cref="Interlocked.CompareExchange{T}(ref T, T, T)" />) when the
/// window rolls over, so no pruning or background cleanup is needed.
/// </summary>
/// <remarks>
/// <para>
/// Two independent limits:
/// <list type="bullet">
/// <item><description><b>Global rate limit</b> — at most <c>maxPerMinute</c> snapshots
/// per window (a single <see cref="Interlocked.Increment(ref int)" />).</description></item>
/// <item><description><b>Per-error dedup</b> — at most <c>maxSameError</c> snapshots for
/// the same error hash per window.</description></item>
/// </list>
/// </para>
/// <para>
/// The window is one minute, not a configurable length. The cap is expressed as
/// <c>OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_MINUTE</c>, matching Java and Python, so a
/// per-minute rate is the whole meaning of the setting — a separate period control would let the two
/// disagree about what "per minute" means.
/// </para>
/// <para>
/// The error hash keys on operation + exception type + throw-site method, matching Java and Python.
/// It deliberately <b>excludes the exception message</b>, so request-specific data in messages (ids,
/// timestamps) can't explode hash cardinality, while the throw-site origin still keeps genuinely
/// distinct failures apart: without it, two unrelated errors sharing an exception type on the same
/// route would collapse into one dedup budget and one of them would be silently suppressed. The
/// origin is bounded — a service has finitely many methods — and stable across deploys, since the
/// source line number is not part of it.
/// </para>
/// </remarks>
internal sealed class IncidentRateLimiter
{
    /// <summary>Maximum distinct error hashes tracked per window (cardinality guard).</summary>
    private const int MaxErrorHashEntries = 1000;

    /// <summary>
    /// Window length. Fixed, because the cap is defined as a per-minute rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows are indexed off a <b>monotonic</b> clock, not wall-clock time, because the index
    /// decides when counters reset. A wall clock can move backwards — an NTP correction is the common
    /// case — which lowers the index, makes the window compare-and-swap install a fresh window, and
    /// resets both the global and per-error counters mid-minute. The cap then admits a burst it was
    /// configured to refuse. A forward jump has the same effect by skipping windows.
    /// </para>
    /// <para>
    /// This is deliberately not what the sibling distros do; they bucket on wall-clock time and are
    /// exposed to the same reset. It matches how the rest of this assembly already treats elapsed
    /// time, though — see <c>ShutdownBudget</c>, which documents the same reasoning for the same
    /// choice, and the endpoint collector's in-flight wait.
    /// </para>
    /// <para>
    /// Only bucketing is affected. Timestamps that reach the wire stay on wall-clock time, because a
    /// consumer needs to correlate them with events outside this process; a monotonic reading is
    /// meaningless off the host.
    /// </para>
    /// </remarks>
    private const long WindowMs = 60_000L;

    private readonly Func<long> nowMs;

    // volatile: updated dynamically from the WATCHER config channel.
    private volatile int maxPerMinute;
    private volatile int maxSameError;

    private Window currentWindow;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentRateLimiter"/> class.
    /// </summary>
    /// <param name="maxPerMinute">Max snapshots per minute (clamped to >= 1).</param>
    /// <param name="maxSameError">Max snapshots per error hash per minute (clamped to >= 1).</param>
    /// <param name="nowMs">
    /// Millisecond clock used only to decide which window an incident falls in. Defaults to
    /// <see cref="Environment.TickCount64" />; see the remarks on <see cref="WindowMs" /> for why it is
    /// monotonic rather than wall-clock. Injectable for tests.
    /// </param>
    public IncidentRateLimiter(int maxPerMinute, int maxSameError, Func<long>? nowMs = null)
    {
        this.maxPerMinute = Math.Max(1, maxPerMinute);
        this.maxSameError = Math.Max(1, maxSameError);
        this.nowMs = nowMs ?? (() => Environment.TickCount64);
        this.currentWindow = new Window(this.CurrentPeriodIndex());
    }

    /// <summary>
    /// Generate a dedup hash from operation + exception type. Excludes the exception
    /// message to bound cardinality. Hex-encoded SHA-256; falls back to the input's hash
    /// code if hashing is unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a dedup key, not a security primitive. The input is an operation name plus an
    /// exception type name, and the value is used only as a dictionary key inside this process — it
    /// is never placed on the <c>IncidentSnapshot</c> model and never reaches the wire.
    /// </para>
    /// <para>
    /// SHA-256 rather than the MD5 the Java distro uses. That is a deliberate divergence: because
    /// the value never leaves the process, cross-distro comparability of the digest is unobservable
    /// and buys nothing, while MD5 is reported as a broken algorithm by the security scanners this
    /// repository runs and would be the only weak-hash use in the codebase — the sampler rules cache
    /// already uses SHA-256. Collision resistance does still matter functionally, though not against
    /// an adversary: two distinct errors colliding would share one dedup budget and silently
    /// suppress one of them.
    /// </para>
    /// </remarks>
    /// <param name="operation">Operation, e.g. <c>"GET /users/{id}"</c>.</param>
    /// <param name="exceptionType">Exception type name, or null for status-only errors.</param>
    /// <param name="originMethod">
    /// Fully-qualified throw-site method, or null/empty when unavailable. From
    /// <c>IncidentSnapshotCollector.ExtractOriginMethod</c>.
    /// </param>
    /// <returns>A stable hex hash string.</returns>
    public static string GenerateErrorHash(string operation, string? exceptionType, string? originMethod)
    {
        // Three branches, matching Java's key construction exactly — including the "op:" and "|exc:"
        // prefixes and the ":" before the origin — so the same failure produces the same grouping
        // across distros. A latency incident has no exception, so it keys on the operation alone.
        string input;
        if (string.IsNullOrEmpty(exceptionType))
        {
            input = "op:" + operation;
        }
        else if (string.IsNullOrEmpty(originMethod))
        {
            input = "op:" + operation + "|exc:" + exceptionType;
        }
        else
        {
            input = "op:" + operation + "|exc:" + exceptionType + ":" + originMethod;
        }

        try
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
        catch (Exception)
        {
            return input.GetHashCode().ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Check the global rate limit. Returns <c>true</c> if the incident is allowed,
    /// <c>false</c> if the per-window cap is exceeded.
    /// </summary>
    /// <returns><c>true</c> when the incident is within the per-minute cap.</returns>
    public bool CheckRateLimit()
    {
        var window = this.GetWindow();
        return window.IncrementGlobalCount() <= this.maxPerMinute;
    }

    /// <summary>
    /// Check per-error deduplication. Returns <c>true</c> if this error hash is still
    /// under its per-window ceiling (and increments it), <c>false</c> otherwise or when
    /// the per-window hash table is full.
    /// </summary>
    /// <remarks>
    /// Callers check this before <see cref="CheckRateLimit" /> so that duplicates do not consume
    /// global slots. This matches the Python and JS distros, which both order the gates this way and
    /// record why: running the rate limit first caused dedup-blocked requests to burn global slots
    /// without producing a snapshot. It is deliberately <b>not</b> Java's order — Java checks the
    /// global rate limit first and dedups second.
    /// <para>
    /// The cost of this order is the mirror image, and it is ours alone: an occurrence counted here
    /// still counts if the global cap then rejects the incident, so a burst that exhausts the global
    /// cap also spends per-error budget on snapshots that were never emitted. Both counters here reset
    /// on the same window boundary, which bounds the effect to one minute. Python and JS avoid it by
    /// splitting the check from the record and recording only once a rate slot is secured; adopting
    /// that split here would remove the cost rather than bound it.
    /// </para>
    /// </remarks>
    /// <param name="errorHash">Hash from <see cref="GenerateErrorHash" />.</param>
    /// <returns>
    /// Which outcome applied. A rejection distinguishes the per-error ceiling from the cardinality
    /// guard, because they mean different things to an operator: the first is this error being noisy,
    /// the second is the <i>service</i> producing more distinct errors than the window can track, and
    /// the second is the one that suggests a raised limit will not help.
    /// </returns>
    public DedupOutcome CheckDeduplication(string errorHash)
        => this.GetWindow().TryRecordError(errorHash, this.maxSameError, MaxErrorHashEntries);

    /// <summary>Update the limits dynamically (from the WATCHER config channel).</summary>
    /// <param name="maxPerMinute">New max-per-minute (clamped to >= 1).</param>
    /// <param name="maxSameError">New per-error ceiling (clamped to >= 1).</param>
    public void UpdateConfig(int maxPerMinute, int maxSameError)
    {
        this.maxPerMinute = Math.Max(1, maxPerMinute);
        this.maxSameError = Math.Max(1, maxSameError);
    }

    /// <summary>Reset all window state. For tests.</summary>
    public void ResetState()
    {
        Volatile.Write(ref this.currentWindow, new Window(this.CurrentPeriodIndex()));
    }

    private long CurrentPeriodIndex() => this.nowMs() / WindowMs;

    /// <summary>
    /// Return the current window, atomically swapping to a fresh one once the period has rolled over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Advances <b>forward only</b>. A window is replaced when the computed index is greater than the
    /// current one, never when it is merely different, so a clock reading that moves backwards keeps
    /// the existing window and its counters rather than installing an empty one.
    /// </para>
    /// <para>
    /// The default clock is monotonic, which makes a backwards reading unreachable in production, so
    /// this is defence in depth rather than the primary guard. It is worth having because the failure
    /// it prevents is silent and in the unsafe direction: resetting mid-window admits a burst the cap
    /// was configured to refuse, and nothing downstream can tell that from a genuine rollover. Staying
    /// in the current window instead keeps limiting until real time catches up, which errs toward
    /// suppressing too much rather than too little.
    /// </para>
    /// </remarks>
    /// <returns>The window incidents should currently be counted against.</returns>
    private Window GetWindow()
    {
        var index = this.CurrentPeriodIndex();
        var window = Volatile.Read(ref this.currentWindow);
        if (index > window.PeriodIndex)
        {
            var fresh = new Window(index);
            Interlocked.CompareExchange(ref this.currentWindow, fresh, window);
            return Volatile.Read(ref this.currentWindow);
        }

        return window;
    }

    /// <summary>
    /// All per-window rate-limiting state, atomically swapped on window boundaries.
    /// </summary>
    /// <remarks>
    /// The counters and the dedup lock are private and the dedup decision lives here rather than in
    /// the caller, so the lock cannot be taken from anywhere else and the counters cannot be
    /// incremented out from under it.
    /// </remarks>
    private sealed class Window
    {
        private readonly object dedupLock = new();

        // Per-error counts. int[1] is a mutable box incremented under dedupLock;
        // the lock-free fast path reads index 0 via Volatile.Read.
        private readonly ConcurrentDictionary<string, int[]> errorCounts = new(StringComparer.Ordinal);

        private int globalCount; // Interlocked

        /// <summary>Initializes a new instance of the <see cref="Window"/> class.</summary>
        /// <param name="periodIndex">The tumbling-window index this instance represents.</param>
        public Window(long periodIndex) => this.PeriodIndex = periodIndex;

        /// <summary>Gets the tumbling-window index this instance represents.</summary>
        public long PeriodIndex { get; }

        /// <summary>Count one incident against the global cap.</summary>
        /// <returns>This incident's 1-based position within the window.</returns>
        public int IncrementGlobalCount() => Interlocked.Increment(ref this.globalCount);

        /// <summary>
        /// Record one occurrence of <paramref name="errorHash" /> if it is still under its ceiling.
        /// </summary>
        /// <param name="errorHash">The dedup key.</param>
        /// <param name="maxSameError">Per-error ceiling for this window.</param>
        /// <param name="maxEntries">Cardinality guard on distinct hashes tracked.</param>
        /// <returns>Which outcome applied; see <see cref="DedupOutcome" />.</returns>
        public DedupOutcome TryRecordError(string errorHash, int maxSameError, int maxEntries)
        {
            // Lock-free fast-path rejection.
            if (this.errorCounts.TryGetValue(errorHash, out var existing) &&
                Volatile.Read(ref existing[0]) >= maxSameError)
            {
                return DedupOutcome.PerErrorLimit;
            }

            lock (this.dedupLock)
            {
                if (!this.errorCounts.TryGetValue(errorHash, out existing))
                {
                    if (this.errorCounts.Count >= maxEntries)
                    {
                        return DedupOutcome.CardinalityGuard;
                    }

                    existing = new int[1];
                    this.errorCounts[errorHash] = existing;
                }

                if (existing[0] < maxSameError)
                {
                    existing[0]++;
                    return DedupOutcome.Admitted;
                }
            }

            return DedupOutcome.PerErrorLimit;
        }
    }
}

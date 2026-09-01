// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>
/// Central registry of active instrumentation configurations and their runtime state.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
internal sealed class InstrumentationRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredInstrumentation> configs = new();

    // TypeName -> instrumentation keys, so the capture hot path resolves a woven call's config by an
    // indexed lookup instead of scanning every registered config. A type can host several
    // instrumented methods, so the value is a set of keys.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> keysByType = new();

    // (TypeName, arity) -> instrumentation keys. The callback only receives (instance, args), never the
    // method name/token, so we disambiguate co-located methods by parameter count: args.Length at
    // capture time. Populated at Apply time (IndexArities) because arity comes from reflecting the loaded
    // type, not from the config. Same-arity methods on one type still collide — the documented residual.
    private readonly ConcurrentDictionary<(string Type, int Arity), ConcurrentDictionary<string, byte>> keysByTypeAndArity = new();

    // Types that have EVER had arities indexed, i.e. types the profiler has actually woven. A TOMBSTONE set:
    // entries are never removed, deliberately.
    //
    // WHY IT EXISTS. Removal drops a config from every index but cannot un-weave the IL, so the removed
    // method's callback keeps firing forever. Resolution is `byTypeAndArity ?? byType`, and once removal takes
    // the type down to exactly ONE remaining config, the type-only fallback starts calling itself
    // "unambiguous" again — so the DELETED method's next call attributes to the SURVIVOR's LocationHash and
    // capture policy. Reproduced by test: a snapshot from the deleted method landed on the surviving probe.
    //
    // WHY IT IS NEVER CLEARED. The woven IL outlives the configuration with no expiry, so the fact that a type
    // was once woven stays true for the life of the process. Keeping the entry after the last config for a
    // type is removed costs one string per instrumented type and closes the same hole for the
    // remove-everything-then-re-add case.
    private readonly ConcurrentDictionary<string, byte> arityIndexedTypes = new();

    public int Count => this.configs.Count;

    /// <summary>
    /// Register or update a configuration. Preserves hit state if the config hasn't changed
    /// (same locationHash + createdAt).
    /// </summary>
    public void Register(InstrumentationConfiguration config)
    {
        this.configs.AddOrUpdate(
            config.InstrumentationKey,
            _ => new RegisteredInstrumentation(config, CreateHitState(config)),
            (_, existing) =>
            {
                if (!HasConfigChanged(existing.Config, config))
                {
                    return existing; // Preserve hit state
                }

                return new RegisteredInstrumentation(config, CreateHitState(config));
            });

        // Keep the TypeName index in sync.
        this.keysByType.GetOrAdd(config.TypeName, _ => new ConcurrentDictionary<string, byte>())[config.InstrumentationKey] = 0;
    }

    /// <summary>
    /// Remove configurations that are no longer in the active set.
    /// Returns the removed configurations (carrying both InstrumentationKey and LocationHash so callers can
    /// forget applied-state by key and clear status-dedup by location hash).
    /// </summary>
    public List<InstrumentationConfiguration> RemoveStale(HashSet<string> activeKeys)
    {
        var removed = new List<InstrumentationConfiguration>();
        foreach (var key in this.configs.Keys)
        {
            if (!activeKeys.Contains(key))
            {
                if (this.configs.TryRemove(key, out var reg))
                {
                    removed.Add(reg.Config);
                    this.RemoveFromTypeIndex(reg.Config.TypeName, key);
                    this.RemoveFromArityIndex(reg.Config.TypeName, key);
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Records, at Apply time, that <paramref name="key"/>'s target method exists at the given parameter
    /// counts on <paramref name="typeName"/>. One config maps to several arities when the method is
    /// overloaded. Lets the capture hot path disambiguate co-located methods by arity.
    /// Returns the FULL set of instrumentation keys in any bucket that now holds more than one key — a
    /// same-arity collision that arity resolution cannot disambiguate (the documented residual). The set
    /// includes both the incoming key and its pre-existing peer(s), so the caller can report ERROR on every
    /// ambiguous config, not just the one that happened to apply second. Empty when there is no collision.
    /// </summary>
    public IReadOnlyCollection<string> IndexArities(string typeName, string key, IReadOnlyCollection<int> arities)
    {
        var collidingKeys = new HashSet<string>();

        // Mark the type as woven BEFORE indexing any arity. From here on, resolution for this type must be
        // exact (type, arity) — see HasArityIndex and arityIndexedTypes. Recorded even when `arities` is
        // empty: the profiler was still asked to weave this type, and it is the weaving, not the arity list,
        // that makes the type-only fallback unsafe.
        this.arityIndexedTypes[typeName] = 0;

        foreach (var arity in arities)
        {
            var bucket = this.keysByTypeAndArity.GetOrAdd((typeName, arity), _ => new ConcurrentDictionary<string, byte>());
            bucket[key] = 0;

            // More than one key in the bucket → every key in it is indistinguishable at capture time
            // (args.Length can't separate them). Surface all of them so both/all sides get an ERROR.
            if (bucket.Count > 1)
            {
                foreach (var member in bucket.Keys)
                {
                    collidingKeys.Add(member);
                }
            }
        }

        return collidingKeys;
    }

    /// <summary>
    /// Resolves the instrumentation key for a woven call by its declaring type name and parameter count.
    /// Returns null when no config's method on that type has the given arity. When two configured methods
    /// on one type share both name-slot and arity this still returns one of them — the documented
    /// residual (same-arity collision), which method identity in the callback would be needed to resolve.
    /// </summary>
    public string? TryResolveKeyByTypeAndArity(string typeName, int arity)
    {
        if (this.keysByTypeAndArity.TryGetValue((typeName, arity), out var keys))
        {
            // First key wins; on the capture hot path, so enumerate rather than allocate via .Keys.First().
            // Same-arity collisions (the documented residual) resolve to one of them nondeterministically.
            foreach (var key in keys.Keys)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the profiler has ever woven this type, i.e. arities were indexed for it at Apply time.
    /// </summary>
    /// <param name="typeName">Fully-qualified type name from the woven call.</param>
    /// <returns>True when resolution for this type must be exact (type, arity).</returns>
    // The guard that makes the type-only fallback safe. Once a type is woven, a call arriving with an arity
    // that is NOT in the index means the config for THAT method is gone — so the only correct answer is
    // "capture nothing". Falling back to type-only there is what misattributed a deleted method's captures to
    // a surviving probe on the same type.
    public bool HasArityIndex(string typeName) => this.arityIndexedTypes.ContainsKey(typeName);

    /// <summary>
    /// Resolves the instrumentation key for a woven call by its declaring type name alone, when that is
    /// unambiguous — i.e. exactly one config targets the type. Returns null if no config targets the type,
    /// OR if two-or-more do (a type-only match would have to guess which, and guessing wrong misattributes
    /// the capture to the wrong probe — worse than dropping it).
    /// </summary>
    /// <param name="typeName">Fully-qualified type name from the woven call.</param>
    /// <returns>The single instrumentation key targeting the type, or null when it is not unambiguous.</returns>
    // Type-only fallback for the arity path: used when the arity index has no entry for a call — e.g. a
    // capture that fires in the window after Register but before the Apply-time IndexArities call, or a
    // registry populated without applying (unit tests). TryResolveKeyByTypeAndArity is the precise path, and
    // this fallback is refused outright for a type that has been woven (see HasArityIndex).
    public string? TryResolveKeyByType(string typeName)
    {
        if (this.keysByType.TryGetValue(typeName, out var keys) && keys.Count == 1)
        {
            // Exactly one config for this type → unambiguous. First key wins; enumerate to avoid the
            // .Keys.First() allocation on the capture hot path.
            foreach (var key in keys.Keys)
            {
                return key;
            }
        }

        return null;
    }

    public RegisteredInstrumentation? Get(string instrumentationKey) =>
        this.configs.TryGetValue(instrumentationKey, out var reg) ? reg : null;

    public IEnumerable<RegisteredInstrumentation> GetAll() => this.configs.Values;

    public bool TryHit(string instrumentationKey) =>
        this.configs.TryGetValue(instrumentationKey, out var reg) && reg.HitState.TryHit();

    private static bool HasConfigChanged(InstrumentationConfiguration existing, InstrumentationConfiguration incoming) =>
        existing.LocationHash != incoming.LocationHash ||
        existing.CreatedAt != incoming.CreatedAt;

    private static HitState CreateHitState(InstrumentationConfiguration config) =>
        new(config.Capture.MaxHits, config.ExpiresAt);

    private void RemoveFromTypeIndex(string typeName, string key)
    {
        if (this.keysByType.TryGetValue(typeName, out var keys))
        {
            keys.TryRemove(key, out _);
            if (keys.IsEmpty)
            {
                this.keysByType.TryRemove(typeName, out _);
            }
        }
    }

    // The arity index is keyed by (type, arity) and a key can occupy several arity buckets (overloads),
    // so on removal we sweep every bucket for this type. Serialized with Register/IndexArities by the
    // Manager's configChangeLock, so enumerating keys here does not race a concurrent writer.
    private void RemoveFromArityIndex(string typeName, string key)
    {
        foreach (var entry in this.keysByTypeAndArity)
        {
            if (entry.Key.Type != typeName)
            {
                continue;
            }

            entry.Value.TryRemove(key, out _);
            if (entry.Value.IsEmpty)
            {
                this.keysByTypeAndArity.TryRemove(entry.Key, out _);
            }
        }
    }
}

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
    }

    /// <summary>
    /// Remove configurations that are no longer in the active set.
    /// Returns the keys that were removed.
    /// </summary>
    public List<string> RemoveStale(HashSet<string> activeKeys)
    {
        var removed = new List<string>();
        foreach (var key in this.configs.Keys)
        {
            if (!activeKeys.Contains(key))
            {
                if (this.configs.TryRemove(key, out _))
                {
                    removed.Add(key);
                }
            }
        }

        return removed;
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
}

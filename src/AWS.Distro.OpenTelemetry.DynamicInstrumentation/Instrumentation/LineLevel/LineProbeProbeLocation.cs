// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// One applied line probe: the id baked into its injected IL, paired with the location it captures.
/// </summary>
/// <param name="ProbeId">The id the injected IL passes back to the callback.</param>
/// <param name="Location">The resolved location, carrying the captured local's name, slot, and type.</param>
// Exists because multi-local capture applies N probes at ONE offset, and the callback receives only the
// probeId — nothing else. So each id has to be paired with its own local at apply time or the hit cannot be
// attributed to a variable name. A bare list of locations would not be enough: the ids are allocated one at
// a time and are not contiguous with the config's first id.
internal sealed record LineProbeProbeLocation(int ProbeId, LineProbeLocation Location);

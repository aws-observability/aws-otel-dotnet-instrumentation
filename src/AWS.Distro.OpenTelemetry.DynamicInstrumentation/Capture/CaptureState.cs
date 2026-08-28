// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>
/// Stashed in the profiler's CallTargetState between OnMethodBegin and OnMethodEnd. Carries one
/// <see cref="CaptureEntry"/> per configuration that owns the call, each with its own per-call id so every
/// (possibly recursive) invocation pairs with its own entries.
/// </summary>
/// <param name="Entries">One entry per configuration targeting the woven method.</param>
// PLURAL, because a method can carry more than one configuration — a PROBE and a BREAKPOINT, each with its
// own LocationHash, capture policy and MaxHits budget. The single-key version silently served whichever
// config registered first.
internal sealed record CaptureState(CaptureEntry[] Entries);

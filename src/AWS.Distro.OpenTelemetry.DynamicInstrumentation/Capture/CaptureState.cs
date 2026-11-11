// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>
/// Stashed in the profiler's CallTargetState between OnMethodBegin and OnMethodEnd. Carries the
/// instrumentation key plus a per-call id so each (possibly recursive) invocation pairs with its own
/// entry in <see cref="DIDataStore"/>.
/// </summary>
/// <param name="InstrumentationKey">The instrumentation key of the woven config.</param>
/// <param name="CallId">Unique id for this invocation, issued by <see cref="DIDataStore.RecordEntry"/>.</param>
internal sealed record CaptureState(string InstrumentationKey, long CallId);

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>
/// One configuration's stake in a single woven invocation: which instrumentation it is, and which
/// <see cref="DIDataStore"/> entry holds the arguments captured for it.
/// </summary>
/// <param name="InstrumentationKey">The instrumentation key of the woven config.</param>
/// <param name="CallId">Unique id for this invocation, issued by <see cref="DIDataStore.RecordEntry"/>.</param>
internal readonly record struct CaptureEntry(string InstrumentationKey, long CallId);

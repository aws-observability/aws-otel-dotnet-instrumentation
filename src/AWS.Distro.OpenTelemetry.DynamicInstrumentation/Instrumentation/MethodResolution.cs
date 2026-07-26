// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>
/// Result of resolving a target method: the declaring assembly's simple name and the
/// distinct parameter counts across all overloads sharing the method name.
/// </summary>
internal sealed record MethodResolution(string AssemblyName, IReadOnlyCollection<int> Arities);

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Result of resolving a line-level configuration against a target assembly's debug information:
/// the method to weave, the interior IL offset to inject at, and the local slot to read.
/// </summary>
/// <param name="MethodToken">The metadata token (mdMethodDef) of the method containing the line.</param>
/// <param name="AssemblyName">Simple name of the assembly declaring the method.</param>
/// <param name="TypeName">Fully-qualified name of the declaring type, in the form the native side resolves.</param>
/// <param name="MethodName">Name of the method to weave.</param>
/// <param name="ParameterCount">Parameter count of the resolved method; the native side matches on arity + 1.</param>
/// <param name="IlOffset">The interior IL offset to inject the probe at. Guaranteed to be an instruction
/// boundary, not a branch target, and positioned so the requested local is already assigned.</param>
/// <param name="LocalSlot">The local variable slot to read, or -1 when no local was requested/resolved.</param>
/// <param name="LocalName">The source name of the local being captured, or null.</param>
internal sealed record LineProbeLocation(
    int MethodToken,
    string AssemblyName,
    string TypeName,
    string MethodName,
    int ParameterCount,
    uint IlOffset,
    int LocalSlot,
    string? LocalName);

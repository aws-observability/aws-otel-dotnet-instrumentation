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
/// <param name="LocalTypeName">
/// Assembly-qualified-free full name of the local's declared type (e.g. <c>System.String</c>), or null when
/// no local is being captured. The native rewriter needs this to emit the right <c>box</c> token: a
/// value-type local must be boxed against ITS OWN type, and boxing against the wrong one is undefined
/// behavior rather than a clean failure.
/// </param>
/// <param name="LocalIsValueType">
/// Whether the local's declared type is a value type. This decides whether a <c>box</c> is emitted at all —
/// a reference-type local is already an <c>object</c> reference and boxing one is invalid IL.
/// </param>
/// <param name="HoistedFieldToken">
/// Non-zero when the captured variable lives in a FIELD of an async/iterator state machine rather than in a
/// local slot. The native side then emits <c>ldarg.0; ldfld &lt;token&gt;</c> instead of <c>ldloc</c>.
/// </param>
// ASYNC: for a state-machine method, TypeName/MethodName/ParameterCount/MethodToken describe the COMPILER-
// GENERATED MoveNext, not the method the operator named — the user's source lines only exist there. LocalName
// stays the SOURCE name so the snapshot reads as the operator wrote it, which is the whole reason the two are
// separate fields.
internal sealed record LineProbeLocation(
    int MethodToken,
    string AssemblyName,
    string TypeName,
    string MethodName,
    int ParameterCount,
    uint IlOffset,
    int LocalSlot,
    string? LocalName,
    string? LocalTypeName = null,
    bool LocalIsValueType = false,
    uint HoistedFieldToken = 0);

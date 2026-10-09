// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Managed callbacks invoked by the IL the native profiler injects mid-method for line-level probes.
/// </summary>
/// <remarks>
/// <para>
/// MUST be public, AND so must every callback method — this differs from the function-level
/// <c>DiIntegrationN</c> types, whose callbacks are deliberately internal. The reason is the invocation
/// mechanism, not style: function-level callbacks are bound REFLECTIVELY by the profiler (NonPublic is
/// fine), whereas line-level IL contains a direct <c>call</c> to a MemberRef emitted INTO THE CUSTOMER'S
/// ASSEMBLY (line_probe.cpp: <c>CallMember(callbackMemberRef, is_virtual: false)</c>). A non-public member
/// is inaccessible from that assembly and throws <see cref="MethodAccessException"/> at the first woven
/// call — the same failure the function-level type hit once already, one level deeper.
/// </para>
/// <para>
/// Every member is static: the native side builds the signature with
/// <c>IMAGE_CEE_CS_CALLCONV_DEFAULT</c> and never sets <c>HASTHIS</c> (verified — zero occurrences in
/// line_probe.cpp), so the emitted <c>call</c> passes no instance.
/// </para>
/// <para>
/// SIGNATURES ARE LOAD-BEARING AND UNCHECKED. The native side derives the arity from
/// <c>emissionMode</c> and emits a MemberRef against a hardcoded signature blob:
/// <list type="bullet">
/// <item><description>two-arg: <c>ELEMENT_TYPE_VOID, I4, OBJECT</c> → <c>void (int, object)</c></description></item>
/// <item><description>one-arg: <c>ELEMENT_TYPE_VOID, I4</c> → <c>void (int)</c></description></item>
/// <item><description>gate: <c>ELEMENT_TYPE_BOOLEAN, I4</c> → <c>bool (int)</c></description></item>
/// </list>
/// Nothing validates that a matching managed method exists: <c>DefineMemberRef</c> SUCCEEDS against a
/// signature no method has, and the call then binds to nothing at runtime. So renaming or re-signing
/// anything here silently breaks weaving, which is why <see cref="LineProbeTranslator"/> pins the names as
/// constants and the tests assert the reflected signatures.
/// </para>
/// </remarks>
public static class DiLineIntegration
{
    /// <summary>
    /// Invoked by injected IL when a line probe with no local capture is reached
    /// (<see cref="LineProbeEmissionMode.Legacy"/>).
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL by <see cref="LineProbeTranslator"/>.</param>
    // NoInlining: this is the direct target of a `call` woven into arbitrary customer methods. Letting the
    // JIT inline it would fold DI's frames into the customer's, which corrupts the stack-trace depth the
    // snapshot layer reports.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Probe(int probeId)
    {
        // No try/catch AROUND a body that cannot throw — but see CaptureLocal for why the guard exists at
        // all once real work appears here.
        DiLineIntegrationHelper.OnLineReached(probeId, hasValue: false, value: null);
    }

    /// <summary>
    /// Invoked by injected IL when a line probe captures one local
    /// (<see cref="LineProbeEmissionMode.LocalCapture"/>).
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <param name="value">The captured local, already boxed by the injected IL.</param>
    // The `object` parameter is pre-boxed on the native side (`Box(systemInt32TypeRef)`), so this must NOT
    // box again and must tolerate whatever arrives — including null.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CaptureLocal(int probeId, object value)
    {
        DiLineIntegrationHelper.OnLineReached(probeId, hasValue: true, value: value);
    }

    /// <summary>
    /// Rate-limit gate invoked by injected IL before the capture callback
    /// (<see cref="LineProbeEmissionMode.GatedBox"/>).
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <returns>True to proceed with capture; false to skip it.</returns>
    // Returning false must be CHEAP and must not disturb the customer's evaluation stack: the injected IL is
    // `call ShouldCapture; brfalse.s SKIP`, so a false simply branches past the capture sequence.
    //
    // FAIL CLOSED. Any failure inside the gate returns false — dropping a snapshot is a data-quality issue,
    // while letting an exception escape into customer code at an arbitrary interior offset is a correctness
    // and stability one.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ShouldCapture(int probeId)
    {
        return DiLineIntegrationHelper.ShouldCapture(probeId);
    }
}

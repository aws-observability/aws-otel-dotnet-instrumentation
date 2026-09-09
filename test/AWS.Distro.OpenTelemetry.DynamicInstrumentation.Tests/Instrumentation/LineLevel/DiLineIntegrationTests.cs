// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Locks <see cref="DiLineIntegration"/>'s shape against the MemberRefs the native profiler emits.
/// </summary>
// WHY REFLECTION AND NOT JUST CALLING THEM. Nothing on either side of this boundary is type-checked. The
// native profiler builds a MemberRef from a hardcoded signature blob and DefineMemberRef SUCCEEDS even when
// no managed method matches — the call then binds to nothing at runtime. So the compiler cannot catch a
// rename, an added parameter, a dropped `static`, or a narrowed accessibility; only an assertion on the
// reflected member can. A test that merely CALLS these methods would still pass after any such change.
public class DiLineIntegrationTests
{
    private static readonly Type IntegrationType = typeof(DiLineIntegration);

    [Fact]
    public void Type_IsPublic_BecauseCustomerIlCallsItDirectly()
    {
        // The native side emits `call` to this type from inside the TARGET assembly
        // (line_probe.cpp: CallMember(callbackMemberRef, is_virtual: false)). A non-public type is
        // inaccessible from there and throws MethodAccessException at the first woven call.
        IntegrationType.IsPublic.Should().BeTrue(
            "injected IL in the customer's assembly calls this type directly");
    }

    [Theory]
    [InlineData(LineProbeTranslator.ProbeMethod)]
    [InlineData(LineProbeTranslator.CaptureMethod)]
    [InlineData(LineProbeTranslator.GateMethod)]
    public void Callback_IsPublicAndStatic(string methodName)
    {
        var method = IntegrationType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull(
            "{0} is named in a MemberRef emitted into customer IL; a missing or non-public member binds " +
            "to nothing at runtime rather than failing at build time",
            methodName);
        method!.IsStatic.Should().BeTrue(
            "the native signature uses IMAGE_CEE_CS_CALLCONV_DEFAULT and never sets HASTHIS, so no " +
            "instance is passed");
    }

    [Fact]
    public void Probe_HasTheOneArgSignatureNativeEmitsForLegacyMode()
    {
        // Native: ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4  ->  void Probe(int32)
        var method = IntegrationType.GetMethod(
            LineProbeTranslator.ProbeMethod, BindingFlags.Public | BindingFlags.Static);

        method!.ReturnType.Should().Be(typeof(void));
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(1, "Legacy mode emits a one-arg callback");
        parameters[0].ParameterType.Should().Be(typeof(int), "ELEMENT_TYPE_I4");
    }

    [Fact]
    public void CaptureLocal_HasTheTwoArgSignatureNativeEmitsForLocalCapture()
    {
        // Native: ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4, ELEMENT_TYPE_OBJECT -> void CaptureLocal(int32, object)
        var method = IntegrationType.GetMethod(
            LineProbeTranslator.CaptureMethod, BindingFlags.Public | BindingFlags.Static);

        method!.ReturnType.Should().Be(typeof(void));
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(2, "LocalCapture, box and async-hoisted modes emit a two-arg callback");
        parameters[0].ParameterType.Should().Be(typeof(int), "ELEMENT_TYPE_I4");
        parameters[1].ParameterType.Should().Be(
            typeof(object),
            "ELEMENT_TYPE_OBJECT — the injected IL boxes the local BEFORE the call, so a typed parameter " +
            "would not match the emitted signature");
    }

    [Fact]
    public void ShouldCapture_HasTheGateSignatureNativeEmitsForGatedBox()
    {
        // Native: ELEMENT_TYPE_BOOLEAN, ELEMENT_TYPE_I4  ->  bool ShouldCapture(int32)
        var method = IntegrationType.GetMethod(
            LineProbeTranslator.GateMethod, BindingFlags.Public | BindingFlags.Static);

        method!.ReturnType.Should().Be(
            typeof(bool), "ELEMENT_TYPE_BOOLEAN; the injected IL follows the call with brfalse.s");
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(1);
        parameters[0].ParameterType.Should().Be(typeof(int), "ELEMENT_TYPE_I4");
    }

    [Fact]
    public void CallbackNames_MatchWhatTheTranslatorPutsInTheDefinition()
    {
        // The translator writes these strings into the native definition. If the two ever disagree, weaving
        // silently targets a method that does not exist — so assert against the translator's own constants
        // rather than re-typing the literals here.
        LineProbeTranslator.ProbeMethod.Should().Be("Probe");
        LineProbeTranslator.CaptureMethod.Should().Be("CaptureLocal");
        LineProbeTranslator.GateMethod.Should().Be("ShouldCapture");
        LineProbeTranslator.CallbackType.Should().Be(
            IntegrationType.FullName, "the definition's callbackType must name this exact type");
        LineProbeTranslator.CallbackAssembly.Should().Be(
            IntegrationType.Assembly.GetName().Name, "the definition's callbackAssembly must name this assembly");
    }

    [Fact]
    public void PublicSurface_IsExactlyTheThreeWovenCallbacks()
    {
        // Anything else public on this type is reachable from customer IL and becomes part of a contract we
        // did not intend to make. Helper logic belongs on DiLineIntegrationHelper.
        var publicMethods = IntegrationType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        publicMethods.Should().BeEquivalentTo(new[] { "CaptureLocal", "Probe", "ShouldCapture" });
    }
}

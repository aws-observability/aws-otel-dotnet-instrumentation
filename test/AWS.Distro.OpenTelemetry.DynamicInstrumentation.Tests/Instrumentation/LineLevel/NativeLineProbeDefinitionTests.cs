// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Tests for <c>NativeLineProbeDefinition</c>: the marshaled memory contract with the native profiler's
/// <c>_LineProbeDefinition</c>, and the hand-allocated signature array's alloc/free discipline.
/// </summary>
// WHY THESE TESTS EXIST AT ALL: this struct is the one place in line-level where a mistake produces
// memory corruption instead of an exception. There is no version tag on the wire, no field-name
// negotiation, and no error if the two sides disagree — a single reordered or resized field shifts every
// later field's offset and the native side reads a string pointer out of the middle of an integer.
// Nothing in the compiler, the marshaler, or a happy-path E2E run can catch that, so it is asserted here.
public class NativeLineProbeDefinitionTests
{
    // The marshaled offsets of `_LineProbeDefinition` (line_probe.h), 64-bit. These are MEASURED from
    // Marshal.OffsetOf on this exact field order, not computed by hand — hand-computed padding is how a
    // layout assertion ends up agreeing with a wrong model instead of with the real ABI.
    //
    // Read this as: "the native header's field order, in bytes." Reordering, inserting, or resizing any
    // managed field moves at least one of these numbers and fails this test loudly, which is the entire
    // point — the alternative is discovering it as a segfault in a customer's process.
    private static readonly (string Field, int Offset)[] ExpectedLayout =
    [
        ("TargetAssembly", 0),            // WCHAR*
        ("TargetType", 8),                // WCHAR*
        ("TargetMethod", 16),             // WCHAR*
        ("TargetSignatureTypes", 24),     // WCHAR**
        ("TargetSignatureTypesLength", 32), // USHORT
        ("IlOffset", 36),                 // ULONG  (note: 2 bytes of padding after the USHORT)
        ("ProbeId", 40),                  // INT32
        ("HoistedFieldToken", 44),        // ULONG
        ("CallbackAssembly", 48),         // WCHAR*
        ("CallbackType", 56),             // WCHAR*
        ("CallbackMethod", 64),           // WCHAR*
        ("EmissionMode", 72),             // INT32
        ("BoxValue", 76),                 // INT32
        ("GateMethod", 80),               // WCHAR*
    ];

    private const int ExpectedSize = 88;

    [Fact]
    public void Layout_MatchesTheNativeLineProbeDefinitionStructExactly()
    {
        foreach (var (field, expectedOffset) in ExpectedLayout)
        {
            Marshal.OffsetOf<NativeLineProbeDefinition>(field).ToInt32().Should().Be(
                expectedOffset,
                "field {0} is part of a raw memory contract with line_probe.h; moving it silently " +
                "corrupts every field after it on the native side",
                field);
        }

        Marshal.SizeOf<NativeLineProbeDefinition>().Should().Be(
            ExpectedSize, "the native side advances by sizeof(LineProbeDefinition) to reach item N+1, so a " +
            "changed stride misreads the whole array, not just one field");
    }

    [Fact]
    public void Layout_DeclaresEveryFieldTheNativeStructHas_AndNoOthers()
    {
        // Guards the gap the offset test alone cannot see: a field APPENDED after GateMethod leaves every
        // asserted offset correct and the struct still "passes" layout, while the native side reads a
        // shorter struct and the array stride diverges. Asserting the full field set closes that.
        var actual = typeof(NativeLineProbeDefinition)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name);

        actual.Should().BeEquivalentTo(
            ExpectedLayout.Select(e => e.Field),
            options => options.WithStrictOrdering(),
            "the managed field set and its ORDER are the ABI; both must mirror line_probe.h");
    }

    [Fact]
    public void Ctor_MarshalsEverySignatureTypeIntoTheUnmanagedArray()
    {
        // A round-trip, not just a non-null check: reads the strings back OUT of the unmanaged block the
        // native side will read, proving they were actually written as Unicode at the right stride.
        var signature = new[] { "_", "System.Int32", "System.String" };

        using var definition = new NativeLineProbeDefinition(
            targetAssembly: "MyApp",
            targetType: "MyApp.Services.OrderService",
            targetMethod: "Process",
            targetSignatureTypes: signature,
            ilOffset: 0x10,
            probeId: 7,
            callbackAssembly: "Callbacks",
            callbackType: "Callbacks.DiLineIntegration",
            callbackMethod: "CaptureLocal");

        definition.TargetSignatureTypesLength.Should().Be((ushort)signature.Length);
        definition.TargetSignatureTypes.Should().NotBe(IntPtr.Zero);

        var readBack = new string?[signature.Length];
        for (int i = 0; i < signature.Length; i++)
        {
            var elementPtr = Marshal.ReadIntPtr(definition.TargetSignatureTypes, i * IntPtr.Size);
            elementPtr.Should().NotBe(IntPtr.Zero, "element {0} must point at allocated unmanaged memory", i);
            readBack[i] = Marshal.PtrToStringUni(elementPtr);
        }

        readBack.Should().Equal(signature, "the native side indexes this array positionally");
    }

    [Fact]
    public void Ctor_DefaultsTheOptionalNativeFieldsToTheLegacyContract()
    {
        // 0/null across the trailing fields is what makes the native side take the unchanged Phase-2 path
        // (line_probe.h: "0/null => LEGACY"). If a default here drifts, an ordinary probe silently starts
        // emitting a different IL sequence.
        using var definition = new NativeLineProbeDefinition(
            "MyApp", "MyApp.Svc", "M", ["_"], 0x10, 1, "Cb", "Cb.T", "Probe");

        definition.EmissionMode.Should().Be((int)LineProbeEmissionMode.Legacy);
        definition.BoxValue.Should().Be(0);
        definition.GateMethod.Should().BeNull();
        definition.HoistedFieldToken.Should().Be(0u, "async hoisted capture is out of scope for v1");
    }

    [Fact]
    public void Ctor_EmptySignatureArray_AllocatesNothingToRead()
    {
        // Marshal.AllocHGlobal(0) is legal and returns a non-null pointer, so the guard that matters is
        // that LENGTH is 0 — the native side loops on the length, and a zero-length loop reads nothing.
        using var definition = new NativeLineProbeDefinition(
            "MyApp", "MyApp.Svc", "M", [], 0x10, 1, "Cb", "Cb.T", "Probe");

        definition.TargetSignatureTypesLength.Should().Be(0);
    }

    [Fact]
    public void Dispose_ReleasesTheArrayAndIsIdempotent()
    {
        // The leak that would otherwise grow on every config poll: this memory is outside the GC's
        // knowledge, so nothing reclaims it if Dispose is skipped or half-completes.
        var definition = new NativeLineProbeDefinition(
            "MyApp", "MyApp.Svc", "M", ["_", "System.Int32"], 0x10, 1, "Cb", "Cb.T", "CaptureLocal");

        definition.TargetSignatureTypes.Should().NotBe(IntPtr.Zero);

        definition.Dispose();

        definition.TargetSignatureTypes.Should().Be(
            IntPtr.Zero, "the pointer must be nulled, or a second Dispose double-frees it");

        // Double-free is heap corruption, not an exception, so idempotence is a correctness requirement
        // rather than a convenience: ApplyLineProbe disposes in a `finally` that can run after a partial
        // failure path has already disposed.
        var second = () => definition.Dispose();
        second.Should().NotThrow("Dispose must be safe to call twice");
    }

    [Fact]
    public void Dispose_DefaultConstructedValue_DoesNotThrow()
    {
        // `default` reaches Dispose whenever an array element was never initialized — e.g. an exception
        // thrown midway through building a batch, with the `finally` still disposing the whole array.
        var definition = default(NativeLineProbeDefinition);

        var dispose = () => definition.Dispose();

        dispose.Should().NotThrow();
    }
}

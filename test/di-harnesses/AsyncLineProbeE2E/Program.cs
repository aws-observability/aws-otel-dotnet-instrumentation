// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// ASYNC line-probe spike harness (DECISION B core). Exit code == number of FAILED assertions.
// Registers ONE hardcoded line probe targeting the compiler-generated MoveNext of an async method,
// with a HOISTED-FIELD token, then runs the async target to completion and asserts the injected
// `ldarg.0; ldfld <hoistedField>; box; call CaptureLocal(int32, object)` fired from inside MoveNext
// and captured the CORRECT value of the hoisted local `y` across the await.

using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.FireCount;
GC.KeepAlive(typeof(LineProbeSink));

Console.WriteLine("=== DI ASYNC Line-Probe Spike (DECISION B core — hoisted local across await) ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run.sh.");
    return 99;
}

// ---- Hardcoded, per-module values (see ASYNC-SPIKE-RESULTS.md for how these were obtained by
// ilspycmd + PDB + System.Reflection.Metadata inspection of THIS built assembly). ----
//   MoveNext lives on the nested state machine type "AsyncSpikeTarget+<Compute>d__0".
//   FindTypeDefByName in the fork resolves ONE level of nesting via the '+' separator.
// Overridable so the same binary drives positive and negative cases.
uint ilOffset = uint.Parse(Environment.GetEnvironmentVariable("ASYNC_IL_OFFSET") ?? "125"); // 0x7D
// 0x04000015 == `<y>5__1` in the CURRENT build. Verified by reflecting the built assembly, not assumed.
// FIELD TOKENS ARE BUILD-DEPENDENT: the old default 0x04000013 was `<>t__builder`
// (AsyncTaskMethodBuilder<int>), so the probe read the builder's first 4 bytes and boxed them as Int32 —
// LastCapturedValue=930373888 instead of 42. Silent wrong value, no crash, no error.
// This affects only this SPIKE, which hardcodes the token. The product never does: it resolves the hoisted
// field by name through PdbReader + the StateMachineHoistedLocalScopes CDI, which is exactly why it survives
// a recompile that renumbers metadata. If this assertion fails again, re-resolve the token before suspecting
// the product.
string moveNextType = Environment.GetEnvironmentVariable("ASYNC_TARGET_TYPE")
    ?? "AsyncSpikeTarget+<Compute>d__0";

// RESOLVED BY REFLECTION, not hardcoded, because a hardcoded token is the single defect this harness has
// hit twice. Roslyn renumbers metadata on any edit to the assembly, so a token that named `<y>5__1` in one
// build names something else in the next — and the failure is SILENT: the probe reads whatever four bytes
// live at that field and boxes them as Int32. The env var stays as an override for the negative case.
uint hoistedFieldToken = ResolveHoistedFieldToken("<y>5__1");

static uint ResolveHoistedFieldToken(string fieldName)
{
    var pinned = Environment.GetEnvironmentVariable("ASYNC_HOISTED_FIELD_TOKEN");
    if (!string.IsNullOrWhiteSpace(pinned))
    {
        Console.WriteLine($"[token] using pinned ASYNC_HOISTED_FIELD_TOKEN={pinned}");
        return Convert.ToUInt32(pinned, 16);
    }

    var stateMachine = typeof(AsyncSpikeTarget).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
        .FirstOrDefault(t => t.Name.StartsWith("<Compute>d__", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("state machine type for Compute not found");

    var field = stateMachine.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public)
        .FirstOrDefault(f => f.Name == fieldName)
        ?? throw new InvalidOperationException(
            $"hoisted field '{fieldName}' not found on {stateMachine.Name}; fields are: " +
            string.Join(", ", stateMachine.GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public).Select(f => f.Name)));

    Console.WriteLine(
        $"[token] resolved {stateMachine.Name}.{fieldName} -> 0x{field.MetadataToken:X8} ({field.FieldType.Name})");
    return (uint)field.MetadataToken;
}
int probeId = 7777;
int computeArg = 41; // Compute(41): y = 42, so the captured hoisted local must equal 42.
int expectedCaptured = computeArg + 1;

var def = new NativeLineProbeDefinition(
    targetAssembly: "AsyncLineProbeE2E",
    targetType: moveNextType,
    targetMethod: "MoveNext",
    targetSignatureTypes: new[] { "System.Void" }, // ret only; MoveNext takes 0 args -> arity match
    ilOffset: ilOffset,
    probeId: probeId,
    callbackAssembly: "LineProbeSink",
    callbackType: "LineProbeSinkNs.LineProbeSink",
    callbackMethod: "CaptureLocal",
    hoistedFieldToken: hoistedFieldToken);

try
{
    var arr = new[] { def };
    NativeMethods.AddLineProbes("async-lineprobe-spike", arr, arr.Length);
    Console.WriteLine($"[reg] AddLineProbes: {moveNextType}.MoveNext @ IL offset {ilOffset} "
        + $"(0x{ilOffset:X}), hoistedFieldToken=0x{hoistedFieldToken:X8} -> "
        + $"LineProbeSink.CaptureLocal(probeId={probeId})");
}
finally
{
    def.Dispose();
}

Console.WriteLine("[rejit] Waiting 700ms for ReJIT of MoveNext...\n");
Thread.Sleep(700);

int failures = 0;
void Assert(int num, string name, Func<bool> check, string detail)
{
    bool ok;
    try { ok = check(); }
    catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; }
    if (ok) { Console.WriteLine($"[PASS] Assertion {num}: {name}"); }
    else { failures++; Console.WriteLine($"[FAIL] Assertion {num}: {name} -- {detail}"); }
}

// ---- Assertion 1: async method runs to completion (no InvalidProgramException / CLR crash). ----
// An invalid MoveNext body throws InvalidProgramException at first JIT of the state machine.
LineProbeSink.FireCount = 0;
LineProbeSink.LastProbeId = -1;
LineProbeSink.LastCapturedValue = null;

int result = -1;
Exception? computeEx = null;
try
{
    result = AsyncSpikeTarget.Compute(computeArg).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    computeEx = ex;
}
Assert(1, "async Compute runs to completion (no InvalidProgramException / CLR crash in MoveNext)",
    () => computeEx == null,
    computeEx == null ? "" : computeEx.GetType().FullName + ": " + computeEx.Message);

// ---- Assertion 4: the async method still returns the correct result across the await. ----
// Compute(41): y=42, z=y*2=84.
Assert(4, $"Compute({computeArg}) == {expectedCaptured * 2} (result unchanged across the await)",
    () => result == expectedCaptured * 2,
    $"got {result}");

// ---- Assertion 2: capture fires from INSIDE MoveNext. ----
Assert(2, "CaptureLocal fired from inside MoveNext (FireCount > 0 after one async call)",
    () => LineProbeSink.FireCount > 0,
    $"FireCount={LineProbeSink.FireCount}");

// ---- Assertion 3 (make-or-break): capture received the CORRECT hoisted-local value. ----
// For Compute(41), the hoisted `y` == 42. Reading garbage / a wrong slot fails here.
Assert(3, $"CaptureLocal received the correct hoisted local y == {expectedCaptured} (read across await)",
    () => LineProbeSink.LastCapturedValue is int iv && iv == expectedCaptured,
    $"LastCapturedValue={LineProbeSink.LastCapturedValue ?? "(null)"} "
        + $"(type={LineProbeSink.LastCapturedValue?.GetType().Name ?? "null"})");

Console.WriteLine($"[info] LastProbeId={LineProbeSink.LastProbeId} (expected {probeId}); "
    + $"LastCapturedValue={LineProbeSink.LastCapturedValue ?? "(null)"}; FireCount={LineProbeSink.FireCount}");

Console.WriteLine($"\n=== Result: {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
return failures;

// ------------------------------------------------------------------
// P/Invoke surface for the forked native export (mirrors LineProbeE2E discipline).
// ------------------------------------------------------------------
internal static class NativeMethods
{
    private const string NativeLib = "OpenTelemetry.AutoInstrumentation.Native";

    [DllImport(NativeLib, EntryPoint = "AddLineProbes")]
    public static extern void AddLineProbes(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeLineProbeDefinition[] items,
        int size);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeLineProbeDefinition : IDisposable
{
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetType;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetMethod;
    public IntPtr TargetSignatureTypes;
    public ushort TargetSignatureTypesLength;
    public uint IlOffset;
    public int ProbeId;
    // ASYNC SPIKE: nonzero mdFieldDef => read that hoisted local off the async state machine.
    public uint HoistedFieldToken;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackType;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackMethod;
    // BOX-GATE SPIKE (DECISION A) trailing fields; 0/null => legacy behavior. Must match native
    // struct stride (line_probe.h). This async harness only uses legacy (mode 0).
    public int EmissionMode;
    public int BoxValue;
    [MarshalAs(UnmanagedType.LPWStr)] public string GateMethod;

    // ADDED 2026-08-20: the product struct grew these two trailing fields for non-int local capture
    // (88 -> 104 bytes, 16 fields). Without them the native side reads localTypeName as a pointer past the
    // end of this struct and the process dies with SIGBUS inside AddLineProbes. LocalIsValueType must be 1
    // for an int local or the `box` is suppressed and the woven IL is invalid (InvalidProgramException),
    // because the callback is `void CaptureLocal(int, object)`.
    [MarshalAs(UnmanagedType.LPWStr)] public string? LocalTypeName;
    public int LocalIsValueType;

    public NativeLineProbeDefinition(
        string targetAssembly,
        string targetType,
        string targetMethod,
        string[] targetSignatureTypes,
        uint ilOffset,
        int probeId,
        string callbackAssembly,
        string callbackType,
        string callbackMethod,
        uint hoistedFieldToken = 0,
        int emissionMode = 0,
        int boxValue = 0,
        string gateMethod = null)
    {
        this.TargetAssembly = targetAssembly;
        this.TargetType = targetType;
        this.TargetMethod = targetMethod;
        this.IlOffset = ilOffset;
        this.ProbeId = probeId;
        this.HoistedFieldToken = hoistedFieldToken;
        this.CallbackAssembly = callbackAssembly;
        this.CallbackType = callbackType;
        this.CallbackMethod = callbackMethod;
        this.EmissionMode = emissionMode;
        this.BoxValue = boxValue;
        this.GateMethod = gateMethod;
        this.LocalTypeName = null;
        this.LocalIsValueType = 1;

        this.TargetSignatureTypesLength = (ushort)targetSignatureTypes.Length;
        this.TargetSignatureTypes = Marshal.AllocHGlobal(IntPtr.Size * targetSignatureTypes.Length);
        for (int i = 0; i < targetSignatureTypes.Length; i++)
        {
            Marshal.WriteIntPtr(
                this.TargetSignatureTypes,
                i * IntPtr.Size,
                Marshal.StringToHGlobalUni(targetSignatureTypes[i]));
        }
    }

    public void Dispose()
    {
        if (this.TargetSignatureTypes == IntPtr.Zero)
        {
            return;
        }

        for (int i = 0; i < this.TargetSignatureTypesLength; i++)
        {
            var ptr = Marshal.ReadIntPtr(this.TargetSignatureTypes, i * IntPtr.Size);
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        Marshal.FreeHGlobal(this.TargetSignatureTypes);
        this.TargetSignatureTypes = IntPtr.Zero;
    }
}

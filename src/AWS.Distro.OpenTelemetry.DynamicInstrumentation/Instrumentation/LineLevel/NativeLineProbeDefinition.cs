// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Flat marshaled definition of one line probe, handed to the native profiler's <c>AddLineProbes</c>.
/// </summary>
// FIELD ORDER AND TYPES MUST MATCH `_LineProbeDefinition` in the forked profiler's line_probe.h
// EXACTLY. This is a raw memory contract: there is no negotiation, no version tag, and no error if the
// two sides disagree. A single inserted/reordered/resized field shifts every subsequent field's offset,
// and the native side then reads a string pointer out of the middle of an integer — i.e. memory
// corruption or a hard crash, not a friendly exception.
//
// The native struct, for reference (line_probe.h):
//   WCHAR* targetAssembly; WCHAR* targetType; WCHAR* targetMethod;
//   WCHAR** signatureTypes; USHORT signatureTypesLength;
//   ULONG ilOffset; INT32 probeId; ULONG hoistedFieldToken;
//   WCHAR* callbackAssembly; WCHAR* callbackType; WCHAR* callbackMethod;
//   INT32 emissionMode; INT32 boxValue; WCHAR* gateMethod;
//
// NAMES, NOT TOKENS (Q3): the target is located by assembly/type/method + signature COUNT, and the
// native side builds the cross-assembly MemberRef for the callback itself. Metadata tokens are
// per-module, and the callback lives in a different module than the target, so a token would be
// meaningless on the other side.
//
// Deliberately mirrors NativeCallTargetDefinition's proven AllocHGlobal/Dispose discipline rather than
// inventing a new one — the string array is hand-allocated in unmanaged memory the GC does not track,
// so every allocation needs a matching free. Leak it and every config poll grows the process; free it
// early and the native side reads freed memory.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeLineProbeDefinition : IDisposable
{
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetAssembly;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetType;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetMethod;
    public IntPtr TargetSignatureTypes;
    public ushort TargetSignatureTypesLength;
    public uint IlOffset;
    public int ProbeId;

    /// <summary>
    /// Async only: an <c>mdFieldDef</c> in the TARGET's module identifying a hoisted state-machine
    /// field to read. Zero for the sync path. Async is out of scope for v1, so this ships as 0.
    /// </summary>
    public uint HoistedFieldToken;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string CallbackAssembly;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string CallbackType;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string CallbackMethod;

    /// <summary>The IL sequence to emit; see <see cref="LineProbeEmissionMode"/>.</summary>
    public int EmissionMode;

    /// <summary>
    /// Dual-purpose by native design: the local SLOT INDEX for <see cref="LineProbeEmissionMode.LocalCapture"/>,
    /// or the constant int to box for the gated/ungated box modes.
    /// </summary>
    public int BoxValue;

    /// <summary>
    /// Name of the <c>bool ShouldCapture(int32)</c> gate on <see cref="CallbackType"/>. Required by
    /// <see cref="LineProbeEmissionMode.GatedBox"/>; null otherwise.
    /// </summary>
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? GateMethod;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeLineProbeDefinition"/> struct.
    /// </summary>
    /// <param name="targetAssembly">Simple name of the assembly declaring the target method.</param>
    /// <param name="targetType">Fully-qualified target type name.</param>
    /// <param name="targetMethod">Target method name.</param>
    /// <param name="targetSignatureTypes">Signature array: <c>[returnType, param1, ...]</c>. The native
    /// side matches on LENGTH (arity + 1); individual entries may be the <c>"_"</c> wildcard.</param>
    /// <param name="ilOffset">Interior IL offset to inject at. Must already be validated as an
    /// instruction boundary that is not a branch target (see <c>PdbReader</c> / <c>IlBoundaryScanner</c>).</param>
    /// <param name="probeId">Opaque id baked into the injected IL as a constant, so the invoker knows
    /// which probe fired without any type-name resolution.</param>
    /// <param name="callbackAssembly">Assembly containing the managed callback.</param>
    /// <param name="callbackType">Fully-qualified callback type name.</param>
    /// <param name="callbackMethod">Callback method name.</param>
    /// <param name="emissionMode">Which IL sequence to emit.</param>
    /// <param name="boxValue">Local slot index (LocalCapture) or constant to box (box modes).</param>
    /// <param name="gateMethod">Gate method name; required for <see cref="LineProbeEmissionMode.GatedBox"/>.</param>
    /// <param name="hoistedFieldToken">Async hoisted-field token; 0 for the sync path.</param>
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
        LineProbeEmissionMode emissionMode = LineProbeEmissionMode.Legacy,
        int boxValue = 0,
        string? gateMethod = null,
        uint hoistedFieldToken = 0)
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
        this.EmissionMode = (int)emissionMode;
        this.BoxValue = boxValue;
        this.GateMethod = gateMethod;

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

    /// <summary>
    /// Frees the unmanaged signature-type array. Idempotent, and safe on a default-constructed value.
    /// </summary>
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

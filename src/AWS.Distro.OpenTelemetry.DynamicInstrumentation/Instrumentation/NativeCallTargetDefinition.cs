// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeCallTargetDefinition : IDisposable
{
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetAssembly;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetType;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetMethod;
    public IntPtr TargetSignatureTypes;
    public ushort TargetSignatureTypesLength;
    public ushort TargetMinimumMajor;
    public ushort TargetMinimumMinor;
    public ushort TargetMinimumPatch;
    public ushort TargetMaximumMajor;
    public ushort TargetMaximumMinor;
    public ushort TargetMaximumPatch;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string IntegrationAssembly;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string IntegrationType;

    public NativeCallTargetDefinition(
        string targetAssembly,
        string targetType,
        string targetMethod,
        string[] targetSignatureTypes,
        string integrationAssembly,
        string integrationType)
    {
        this.TargetAssembly = targetAssembly;
        this.TargetType = targetType;
        this.TargetMethod = targetMethod;
        this.TargetMinimumMajor = 0;
        this.TargetMinimumMinor = 0;
        this.TargetMinimumPatch = 0;
        this.TargetMaximumMajor = ushort.MaxValue;
        this.TargetMaximumMinor = ushort.MaxValue;
        this.TargetMaximumPatch = ushort.MaxValue;
        this.IntegrationAssembly = integrationAssembly;
        this.IntegrationType = integrationType;

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

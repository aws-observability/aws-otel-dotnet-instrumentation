// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

internal static class NativeMethods
{
    private const string NativeLib = "OpenTelemetry.AutoInstrumentation.Native";

    [DllImport(NativeLib, EntryPoint = "AddInstrumentations")]
    public static extern void AddInstrumentations(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeCallTargetDefinition[] methodArrays,
        int size);

    [DllImport(NativeLib, EntryPoint = "AddDerivedInstrumentations")]
    public static extern void AddDerivedInstrumentations(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeCallTargetDefinition[] methodArrays,
        int size);
}

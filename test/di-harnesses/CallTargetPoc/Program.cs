using System.Runtime.InteropServices;

Console.WriteLine("=== CallTarget DI POC (Real Profiler) ===\n");

// Verify profiler is loaded
var profilingEnabled = Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING");
if (profilingEnabled != "1")
{
    Console.WriteLine("[ERROR] Native profiler not loaded. Run via: ./run-calltarget-poc.sh");
    return;
}

Console.WriteLine("[DI] Native profiler is loaded.\n");

// Step 1: Build the instrumentation definition
// This tells the profiler: "wrap OrderService.ProcessOrder with DiIntegration2 callbacks"
var definition = new NativeCallTargetDefinition(
    targetAssembly: "CallTargetPoc",
    targetType: "OrderService",
    targetMethod: "ProcessOrder",
    targetSignatureTypes: new[] { "System.String", "System.String", "System.Int32" }, // return type + param types
    targetMinimumMajor: 1, targetMinimumMinor: 0, targetMinimumPatch: 0,
    targetMaximumMajor: ushort.MaxValue, targetMaximumMinor: ushort.MaxValue, targetMaximumPatch: ushort.MaxValue,
    integrationAssembly: "CallTargetPoc",
    integrationType: "DiIntegration2"
);

Console.WriteLine("[DI] Built NativeCallTargetDefinition:");
Console.WriteLine($"     Target: {definition.TargetAssembly}.{definition.TargetType}.{definition.TargetMethod}");
Console.WriteLine($"     Integration: {definition.IntegrationType}\n");

// Step 2: Register with the native profiler via P/Invoke
Console.WriteLine("[DI] Calling NativeMethods.AddInstrumentations()...");
try
{
    var definitions = new NativeCallTargetDefinition[] { definition };
    NativeMethods.AddInstrumentations("DI-POC-001", definitions, definitions.Length);
    Console.WriteLine("[DI] SUCCESS — method registered for instrumentation.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"[DI] AddInstrumentations failed: {ex.Message}");
    Console.WriteLine("[DI] This is expected if the types in DiIntegration2 aren't found by the profiler.");
    Console.WriteLine("[DI] In production, DiIntegration2 would be compiled into the distribution DLL.\n");
}

// Step 3: Wait for ReJIT to complete (profiler does it asynchronously)
Console.WriteLine("[DI] Waiting 200ms for profiler to complete ReJIT...");
Thread.Sleep(200);

// Step 4: Call the target method (should now be instrumented)
Console.WriteLine("[App] Calling OrderService.ProcessOrder(\"ORD-123\", 5)...");
var service = new OrderService();
var result = service.ProcessOrder("ORD-123", 5);
Console.WriteLine($"[App] Result: {result}\n");

// Step 4: Check if our integration was called
Console.WriteLine("[DI] === Capture Results ===");
Console.WriteLine($"  OnMethodBegin fired: {DiIntegration2.BeginFired}");
Console.WriteLine($"  OnMethodEnd fired:   {DiIntegration2.EndFired}");
if (DiIntegration2.BeginFired)
{
    Console.WriteLine($"  Captured args:       [{string.Join(", ", DiIntegration2.CapturedArgs ?? Array.Empty<object>())}]");
    Console.WriteLine($"  Captured return:     {DiIntegration2.CapturedReturn}");
}
else
{
    Console.WriteLine("  (Integration not called — profiler may not have found our integration type.");
    Console.WriteLine("   In production, DiIntegration2 is part of the instrumentation DLL loaded by the profiler.)");
}

Console.WriteLine("\n=== POC Complete ===");

// Clean up
definition.Dispose();

// === Target class ===
public class OrderService
{
    public string ProcessOrder(string orderId, int quantity)
    {
        var total = quantity * 9.99;
        return $"Order {orderId}: {quantity} items, total ${total:F2}";
    }
}

// === DiIntegration2: The CallTarget integration (2 params) ===
// Uses the REAL OTel CallTarget types from the profiler's managed assembly.
// The native profiler validates that return types match exactly.
public static class DiIntegration2
{
    public static bool BeginFired;
    public static bool EndFired;
    public static object?[]? CapturedArgs;
    public static object? CapturedReturn;

    internal static OpenTelemetry.AutoInstrumentation.CallTarget.CallTargetState OnMethodBegin<TTarget, TArg1, TArg2>(TTarget instance, TArg1 arg1, TArg2 arg2)
    {
        BeginFired = true;
        CapturedArgs = new object?[] { arg1, arg2 };
        Console.WriteLine($"  [DiIntegration2.OnMethodBegin] instance={instance?.GetType().Name}, arg1={arg1}, arg2={arg2}");
        return OpenTelemetry.AutoInstrumentation.CallTarget.CallTargetState.GetDefault();
    }

    internal static OpenTelemetry.AutoInstrumentation.CallTarget.CallTargetReturn<TReturn> OnMethodEnd<TTarget, TReturn>(
        TTarget instance, TReturn returnValue, Exception? exception, in OpenTelemetry.AutoInstrumentation.CallTarget.CallTargetState state)
    {
        EndFired = true;
        CapturedReturn = returnValue;
        Console.WriteLine($"  [DiIntegration2.OnMethodEnd] return={returnValue}, exception={exception?.Message ?? "null"}");
        return new OpenTelemetry.AutoInstrumentation.CallTarget.CallTargetReturn<TReturn>(returnValue);
    }
}

// === P/Invoke to the native profiler ===
public static class NativeMethods
{
    private const string NativeLib = "OpenTelemetry.AutoInstrumentation.Native";

    [DllImport(NativeLib, EntryPoint = "AddInstrumentations")]
    public static extern void AddInstrumentations(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeCallTargetDefinition[] methodArrays,
        int size);
}

// === NativeCallTargetDefinition (matches the native struct layout) ===
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NativeCallTargetDefinition : IDisposable
{
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetType;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetMethod;
    public IntPtr TargetSignatureTypes;
    public ushort TargetSignatureTypesLength;
    public ushort TargetMinimumMajor;
    public ushort TargetMinimumMinor;
    public ushort TargetMinimumPatch;
    public ushort TargetMaximumMajor;
    public ushort TargetMaximumMinor;
    public ushort TargetMaximumPatch;
    [MarshalAs(UnmanagedType.LPWStr)] public string IntegrationAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string IntegrationType;

    public NativeCallTargetDefinition(
        string targetAssembly, string targetType, string targetMethod,
        string[] targetSignatureTypes,
        ushort targetMinimumMajor, ushort targetMinimumMinor, ushort targetMinimumPatch,
        ushort targetMaximumMajor, ushort targetMaximumMinor, ushort targetMaximumPatch,
        string integrationAssembly, string integrationType)
    {
        TargetAssembly = targetAssembly;
        TargetType = targetType;
        TargetMethod = targetMethod;
        TargetMinimumMajor = targetMinimumMajor;
        TargetMinimumMinor = targetMinimumMinor;
        TargetMinimumPatch = targetMinimumPatch;
        TargetMaximumMajor = targetMaximumMajor;
        TargetMaximumMinor = targetMaximumMinor;
        TargetMaximumPatch = targetMaximumPatch;
        IntegrationAssembly = integrationAssembly;
        IntegrationType = integrationType;

        // Marshal signature types as array of LPWStr pointers
        TargetSignatureTypesLength = (ushort)targetSignatureTypes.Length;
        TargetSignatureTypes = Marshal.AllocHGlobal(IntPtr.Size * targetSignatureTypes.Length);
        for (int i = 0; i < targetSignatureTypes.Length; i++)
        {
            Marshal.WriteIntPtr(TargetSignatureTypes, i * IntPtr.Size,
                Marshal.StringToHGlobalUni(targetSignatureTypes[i]));
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < TargetSignatureTypesLength; i++)
        {
            var ptr = Marshal.ReadIntPtr(TargetSignatureTypes, i * IntPtr.Size);
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
        if (TargetSignatureTypes != IntPtr.Zero) Marshal.FreeHGlobal(TargetSignatureTypes);
    }
}

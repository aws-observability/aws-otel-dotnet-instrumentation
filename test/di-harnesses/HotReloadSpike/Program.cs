using System.Reflection;
using System.Reflection.Metadata;
Console.WriteLine("=== HOT RELOAD / MetadataUpdater CAPABILITY PROBE ===");
Console.WriteLine($"runtime            : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"CORECLR_ENABLE_PROFILING = {Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") ?? "(unset)"}");
Console.WriteLine($"CORECLR_PROFILER         = {Environment.GetEnvironmentVariable("CORECLR_PROFILER") ?? "(unset)"}");
Console.WriteLine($"DOTNET_MODIFIABLE_ASSEMBLIES = {Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES") ?? "(unset)"}");
Console.WriteLine($"Debugger.IsAttached      = {System.Diagnostics.Debugger.IsAttached}");
Console.WriteLine($"MetadataUpdater.IsSupported = {MetadataUpdater.IsSupported}");
var caps = typeof(MetadataUpdater).GetMethod("GetCapabilities", BindingFlags.NonPublic|BindingFlags.Static);
Console.WriteLine($"GetCapabilities()    = {caps?.Invoke(null, null) ?? "(unavailable)"}");
// Prove whether ApplyUpdate is reachable at all: pass empty deltas and report the exact failure.
try { MetadataUpdater.ApplyUpdate(typeof(Program).Assembly, default, default, default); Console.WriteLine("ApplyUpdate(empty)  = returned WITHOUT throwing"); }
catch (Exception ex) { Console.WriteLine($"ApplyUpdate(empty)  = {ex.GetType().Name}: {ex.Message}"); }

// Is the assembly itself EnC-capable? DebuggableAttribute drives DACF_ALLOW_JIT_OPTS
// (ceeload.cpp:590), and an assembly with JIT opts allowed is NEVER EnC-capable.
var dbg = typeof(Program).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>();
Console.WriteLine($"DebuggableAttribute  = {(dbg == null ? "(none)" : $"IsJITTrackingEnabled={dbg.IsJITTrackingEnabled}, IsJITOptimizerDisabled={dbg.IsJITOptimizerDisabled}")}");
Console.WriteLine("=== END PROBE ===");

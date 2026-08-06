// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

internal partial class Build
{
    // The major version the VENDORED native profiler must be compiled with, as
    // -DOTEL_AUTO_VERSION_MAJOR. It is UPSTREAM's major, not this distro's.
    //
    // WHY THEY ARE NOT THE SAME NUMBER. The native profiler finds its managed half by strong
    // name, and it builds that name from this macro at COMPILE time:
    //
    //   version.h:13                  ASSEMBLY_VERSION = STR4(OTEL_AUTO_VERSION_MAJOR, 0, 0, 0)
    //   otel_profiler_constants.h:79  "OpenTelemetry.AutoInstrumentation, Version=" + ASSEMBLY_VERSION
    //                                 + ", Culture=neutral, PublicKeyToken=c0db600a13f60b51"
    //
    // The assembly it must match is OpenTelemetry.AutoInstrumentation.dll — UPSTREAM's, shipped
    // inside the downloaded distribution. So the macro tracks the version of upstream's managed
    // assembly, and it is unrelated to any version WE stamp on OUR assemblies.
    //
    // Note the shape: MinVer emits <major>.0.0.0 for the assembly version, so upstream v1.16.0
    // ships Version=1.0.0.0 — NOT 1.16.0.0. MAJOR is therefore the only part that matters, and
    // pinning the full version here would be wrong, not merely redundant.
    //
    // WHY A CONSTANT AND NOT A DERIVED VALUE. Our own major is also 1 today, so passing ours
    // would work — BY COINCIDENCE. The failure it hides is silent and total: with a mismatched
    // major the native profiler loads normally, then never finds its managed half, so nothing is
    // instrumented and no error names the cause. Hardcoding upstream's major makes the coupling
    // explicit; AssertManagedAssemblyVersionMatchesNativePin is what keeps the constant honest.
    private const int UpstreamAssemblyVersionMajor = 1;

    /// <summary>Base name of the native profiler library, without extension.</summary>
    private const string NativeProfilerLibraryName = "OpenTelemetry.AutoInstrumentation.Native";

    private readonly AbsolutePath nativeProfilerProjectFolder =
        NukeBuild.RootDirectory / "src" / "OpenTelemetry.AutoInstrumentation.Native";

    [Parameter("Build the vendored native profiler from source instead of shipping the stock upstream binary")]
    private readonly bool buildNativeProfiler;

    // Resolved from PATH lazily. Nuke 8.0.0 has no built-in CMake task, no [LazyPathExecutable] (upstream
    // uses that on a newer Nuke), and its [PathExecutable] is marked obsolete in favor of [PathVariable].
    // Verified against the shipped Nuke.Common 8.0.0 assembly rather than assumed.
    //
    // ToolResolver.GetPathTool is called inside the target, not in a field initializer, ON PURPOSE: it
    // throws when cmake is absent from PATH. Resolving eagerly would make every machine that merely runs
    // the default Workflow require a CMake install, even though nothing in that path compiles native code.
    private static Tool CMake => ToolResolver.GetPathTool("cmake");

    /// <summary>
    /// Compiles the vendored native profiler from <c>src/OpenTelemetry.AutoInstrumentation.Native</c>.
    /// </summary>
    // NOT a port of upstream's CompileNativeSrcLinux — deliberately narrower. Upstream's version derives
    // the version from MinVer via VersionHelper; ours passes UpstreamAssemblyVersionMajor, because the
    // macro must carry UPSTREAM's major (see the comment on that constant), not ours.
    //
    // GLIBC FLOOR. The minimum glibc a build can run on is a property of the BUILD HOST's glibc, not of
    // any compiler flag. Measured: upstream's shipped linux-arm64 binary requires at most GLIBC_2.35,
    // while the same source built on a modern host requires GLIBC_2.36 — which would break every
    // customer below 2.36, a regression against a working baseline. So on Linux this target is expected
    // to run INSIDE an old-glibc container (upstream uses ubuntu:16.04, glibc 2.23, verified). Building
    // it on a modern runner produces a technically-working binary with a silently raised floor.
    private Target CompileNativeProfiler => _ => _
        .Executes(() =>
        {
            var buildDirectory = this.nativeProfilerProjectFolder / "build";
            buildDirectory.CreateDirectory();

            // The 3-part version is cosmetic (PROFILER_VERSION, used in logs). MAJOR is the one that is
            // load-bearing: it builds the strong name used to find the managed half.
            var version = this.openTelemetryAutoInstrumentationVersion.TrimStart('v');
            var parts = version.Split('.');
            var minor = parts.Length > 1 ? parts[1] : "0";
            var patch = parts.Length > 2 ? parts[2] : "0";

            CMake(
                arguments: $"../ -DCMAKE_BUILD_TYPE=Release -DOTEL_AUTO_VERSION={version} " +
                           $"-DOTEL_AUTO_VERSION_MAJOR={UpstreamAssemblyVersionMajor} " +
                           $"-DOTEL_AUTO_VERSION_MINOR={minor} -DOTEL_AUTO_VERSION_PATCH={patch}",
                workingDirectory: buildDirectory);

            CMake(
                arguments: "--build . --config Release --parallel",
                workingDirectory: buildDirectory);

            var built = buildDirectory / "bin" / NativeProfilerLibraryFileName();
            if (!built.FileExists())
            {
                throw new InvalidOperationException(
                    $"CMake reported success but '{built}' does not exist. The expected library name is " +
                    $"platform-dependent; check CMAKE_LIBRARY_OUTPUT_DIRECTORY in CMakeLists.txt.");
            }

            Serilog.Log.Information("Built native profiler: {Path} ({Size:N0} bytes)", built, new FileInfo(built).Length);
        });

    /// <summary>Fork-only exports that must resolve, plus one upstream export as a control.</summary>
    private static readonly string[] RequiredNativeExports =
    [
        // Added by our delta. Absent from a stock upstream binary, which is exactly what makes them
        // useful here: they distinguish "our profiler shipped" from "the download shipped".
        "AddLineProbes",
        "RemoveLineProbe",

        // Upstream's own COM entry point. Included as a CONTROL: if this one fails too, the library is
        // broken outright rather than merely missing our delta, which is a different diagnosis.
        "DllGetClassObject",
    ];

    /// <summary>
    /// Loads the built native profiler and asserts every required export resolves.
    /// </summary>
    // THE ONLY CHECK THAT CATCHES THE V2 FAILURE MODE. Proven by mutation, not asserted: removing
    // line_probe.cpp from CMakeLists.txt produces a build that SUCCEEDS (100%, exit 0) and a library that
    // `ldd` calls loadable, while this gate rejects it with
    //   undefined symbol: _ZN5trace33LineProbeRejitHandlerModuleMethod22RemoveLineProbeRequestEi
    // So `ldd` would have shipped that binary. This gate is the thing standing between a green build and
    // a profiler that cannot load in a customer process.
    //
    // WHY NativeLibrary AND NOT A C HARNESS. The library is linked with -Wl,-z,now (verified: BIND_NOW and
    // FLAGS_1: NOW are present in the built .so), so the loader resolves ALL relocations eagerly no matter
    // which flags the caller passes. A managed NativeLibrary.Load is therefore exactly as strict as
    // dlopen(RTLD_NOW) here, and it needs no C toolchain on the machine running the gate — which matters
    // because the four non-native RID jobs have a .NET SDK and nothing else.
    //
    // WHAT IT STILL CANNOT SEE. It runs on the CURRENT machine's glibc. A binary built against a newer
    // glibc than a customer's will pass here and fail there, because the floor is a property of the build
    // host. That is a separate concern, addressed by building in an old-glibc container, and it is why
    // AssertNativeProfilerLoads is necessary but not sufficient on its own.
    private Target AssertNativeProfilerLoads => _ => _
        .After(this.CompileNativeProfiler)
        .Executes(() =>
        {
            var library = this.nativeProfilerProjectFolder / "build" / "bin" / NativeProfilerLibraryFileName();

            if (!library.FileExists())
            {
                throw new InvalidOperationException(
                    $"Cannot run the load gate: '{library}' does not exist. Run " +
                    $"{nameof(this.CompileNativeProfiler)} first.");
            }

            AssertLibraryExportsResolve(library);
        });

    /// <summary>
    /// Loads a native library and asserts every entry in <see cref="RequiredNativeExports"/> resolves.
    /// </summary>
    /// <param name="library">Absolute path to the native library to load.</param>
    private static void AssertLibraryExportsResolve(AbsolutePath library)
    {
        IntPtr handle;
        try
        {
            handle = NativeLibrary.Load(library);
        }
        catch (DllNotFoundException ex)
        {
            // With BIND_NOW this is where a missing source file surfaces: the message carries the
            // unresolved mangled symbol name, which names the translation unit that was left out.
            throw new InvalidOperationException(
                $"The native profiler at '{library}' FAILED TO LOAD. A build can succeed and still " +
                $"produce this: a source file missing from a build list links into a library with an " +
                $"undefined symbol, and ldd reports it as loadable anyway. Loader message: {ex.Message}",
                ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException(
                $"The native profiler at '{library}' is not loadable on this platform or architecture " +
                $"(cross-built for a different RID?). Loader message: {ex.Message}",
                ex);
        }

        try
        {
            var missing = RequiredNativeExports
                .Where(export => !NativeLibrary.TryGetExport(handle, export, out _))
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The native profiler at '{library}' loaded, but these exports do not resolve: " +
                    $"{string.Join(", ", missing)}. If AddLineProbes/RemoveLineProbe are the missing " +
                    $"ones, this is the STOCK upstream binary rather than our vendored build — the swap " +
                    $"in {nameof(ReplaceNativeProfilerInDistribution)} did not happen. Line-level would " +
                    $"then fail silently, because the managed side treats a missing export as a normal " +
                    $"runtime condition.");
            }

            Serilog.Log.Information(
                "Native load gate passed: {Library} loaded and resolved {Count} exports ({Exports})",
                library,
                RequiredNativeExports.Length,
                string.Join(", ", RequiredNativeExports));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// Asserts the pinned <c>OTEL_AUTO_VERSION_MAJOR</c> still matches the major version of the
    /// upstream managed assembly in the downloaded distribution.
    /// </summary>
    // Runs after the distribution is unpacked, because the assembly it reads comes out of that zip.
    //
    // This is the whole point of the pin. A bare constant cannot notice that upstream shipped a new
    // major; this assertion turns that into a build failure at the moment the version moves, which
    // is the only cheap moment. Without it the sequence is: bump the upstream version -> build stays
    // green -> the profiler loads in production -> nothing is ever instrumented.
    private Target AssertManagedAssemblyVersionMatchesNativePin => _ => _
        .After(this.UnpackAutoInstrumentationDistribution)
        .Executes(() =>
        {
            var assemblyPath = this.openTelemetryDistributionFolder / "net" / "OpenTelemetry.AutoInstrumentation.dll";

            if (!assemblyPath.FileExists())
            {
                throw new InvalidOperationException(
                    $"Cannot verify the native version pin: '{assemblyPath}' is missing. " +
                    $"Run {nameof(this.UnpackAutoInstrumentationDistribution)} first.");
            }

            // Reads metadata only; does not load the assembly into this process (it targets a
            // different framework and would fail to load on some hosts).
            var actual = AssemblyName.GetAssemblyName(assemblyPath);
            var actualMajor = actual.Version!.Major;

            if (actualMajor != UpstreamAssemblyVersionMajor)
            {
                throw new InvalidOperationException(
                    $"Native version pin is stale. The vendored native profiler is compiled with " +
                    $"OTEL_AUTO_VERSION_MAJOR={UpstreamAssemblyVersionMajor}, so it looks for " +
                    $"'OpenTelemetry.AutoInstrumentation, Version={UpstreamAssemblyVersionMajor}.0.0.0'. " +
                    $"The distribution actually ships '{actual.FullName}' (major {actualMajor}). " +
                    $"A mismatch does NOT fail loudly at runtime: the profiler loads, never resolves its " +
                    $"managed half, and silently instruments nothing. Update " +
                    $"{nameof(UpstreamAssemblyVersionMajor)} in build/Build.NativeProfiler.cs to " +
                    $"{actualMajor} and rebuild the native profiler for every RID.");
            }

            Serilog.Log.Information(
                "Native version pin OK: OTEL_AUTO_VERSION_MAJOR={Major} matches {FullName}",
                UpstreamAssemblyVersionMajor,
                actual.FullName);
        });

    /// <summary>Platform-specific file name of the native profiler library.</summary>
    /// <returns>The library file name including extension.</returns>
    private static string NativeProfilerLibraryFileName()
    {
        if (EnvironmentInfo.IsWin)
        {
            return NativeProfilerLibraryName + ".dll";
        }

        return EnvironmentInfo.IsOsx
            ? NativeProfilerLibraryName + ".dylib"
            : NativeProfilerLibraryName + ".so";
    }

    /// <summary>
    /// Whether a native profiler was built for THIS platform in this working tree.
    /// </summary>
    /// <returns>True when the built library exists on disk.</returns>
    // The single source of truth for "should the native swap and its gate run". Both call it, so they can
    // never disagree — a state where the file is replaced but not verified is unreachable by construction.
    private bool BuiltNativeProfilerExists()
    {
        return (this.nativeProfilerProjectFolder / "build" / "bin" / NativeProfilerLibraryFileName())
            .FileExists();
    }

    /// <summary>
    /// Replaces the stock upstream native profiler in the unpacked distribution with the one built from
    /// our vendored source.
    /// </summary>
    // WHY THE DOWNLOAD STILL HAPPENS. Vendoring the native source does not remove the need for
    // DownloadAutoInstrumentationDistribution: the zip also supplies managed assemblies we do not build
    // (OpenTelemetry.AutoInstrumentation.dll and friends). So this is a SURGICAL single-file swap into an
    // otherwise-upstream distribution, not a replacement of it.
    //
    // WHY OVERWRITING NOTHING IS A HARD FAILURE. If the destination is absent, our binary never ships and
    // the distribution silently keeps the stock profiler — which does not export AddLineProbes. The
    // managed side handles that export being missing GRACEFULLY, by design
    // (LineProbeTranslator catches EntryPointNotFoundException and returns
    // ProfilerMissingLineProbeSupport). That is correct behavior at runtime and a disaster as a build
    // outcome: line-level would simply never fire, with no error anywhere in the build. Hence the
    // existence check, and hence it throws instead of warning.
    private Target ReplaceNativeProfilerInDistribution => _ => _
        .After(this.UnpackAutoInstrumentationDistribution)
        .After(this.CompileNativeProfiler)
        .Before(this.PackAWSDistribution)
        // Only RIDs whose CI job builds a native profiler have one to swap in; the rest legitimately ship
        // upstream's binary and simply do not support line-level. So the target no-ops when no build output
        // is present, and RUNS whenever one is.
        //
        // The condition is deliberately "does the built library exist on disk" rather than a flag. A flag
        // can be forgotten, and the failure would be silent in the worst way: a job that compiled the native
        // profiler, skipped the swap, and shipped the stock binary anyway — which is precisely the bug that
        // bit me during development. Keying on the artifact means the swap happens exactly when it can.
        .OnlyWhenDynamic(() => this.BuiltNativeProfilerExists())
        .Executes(() =>
        {
            var fileName = NativeProfilerLibraryFileName();
            var source = this.nativeProfilerProjectFolder / "build" / "bin" / fileName;

            // The distribution holds exactly one RID folder per zip (verified: the macOS zip contains only
            // osx-arm64), so search rather than hardcode a RID. Any match is a file the CLR could load as
            // CORECLR_PROFILER_PATH, so every one of them must be replaced, not just the first.
            var targets = this.openTelemetryDistributionFolder
                .GlobFiles($"**/{fileName}")
                .ToList();

            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to continue: found no '{fileName}' in " +
                    $"'{this.openTelemetryDistributionFolder}' to replace. Shipping the stock profiler " +
                    $"would disable line-level instrumentation SILENTLY — the managed side treats a " +
                    $"missing AddLineProbes export as a normal runtime condition, so nothing would " +
                    $"report an error.");
            }

            foreach (var target in targets)
            {
                Serilog.Log.Information(
                    "Replacing stock native profiler: {Target} ({OldSize:N0} -> {NewSize:N0} bytes)",
                    target,
                    new FileInfo(target).Length,
                    new FileInfo(source).Length);

                FileSystemTasks.CopyFile(source, target, FileExistsPolicy.Overwrite);
            }

            // Debug symbols are emitted as a SEPARATE stripped file by CMakeLists.txt (objcopy
            // --only-keep-debug + --add-gnu-debuglink). Copy it when present so stack traces remain
            // symbolizable; its absence is not fatal (macOS does not produce one).
            var sourceDebug = this.nativeProfilerProjectFolder / "build" / "bin" / (fileName + ".debug");
            if (sourceDebug.FileExists())
            {
                foreach (var target in targets)
                {
                    FileSystemTasks.CopyFile(
                        sourceDebug, target.Parent / (fileName + ".debug"), FileExistsPolicy.Overwrite);
                }
            }
        });

    /// <summary>
    /// Loads the native profiler AS SHIPPED in the distribution folder and asserts its exports resolve.
    /// </summary>
    // Distinct from AssertNativeProfilerLoads, and both are needed. That one gates the BUILD OUTPUT; this
    // one gates the ARTIFACT THAT SHIPS. The gap between them is real and I hit it: during development
    // ReplaceNativeProfilerInDistribution silently did not run, the build output was correct, and the
    // distribution still carried the stock upstream binary. Everything was green.
    //
    // Checking file presence is not enough to catch that — upstream's zip already contains both the .so and
    // a .so.debug, so "the file is there" is true before and after a swap that never happened. Only loading
    // the shipped file and resolving AddLineProbes distinguishes the two.
    private Target AssertShippedNativeProfilerLoads => _ => _
        .After(this.ReplaceNativeProfilerInDistribution)
        .Before(this.PackAWSDistribution)
        // Same condition as the swap, for the same reason: on a RID that ships the stock upstream binary
        // there is no delta to verify, and the fork-only exports are EXPECTED to be absent. Running the
        // gate there would fail every non-native job.
        //
        // Note what this means: a job that does not build native code gets no load gate at all. That is
        // acceptable only because such a job ships a binary upstream already tested. The moment a RID starts
        // building its own, this gate starts guarding it — automatically, by the same disk check.
        .OnlyWhenDynamic(() => this.BuiltNativeProfilerExists())
        .Executes(() =>
        {
            var fileName = NativeProfilerLibraryFileName();
            var shipped = this.openTelemetryDistributionFolder.GlobFiles($"**/{fileName}").ToList();

            if (shipped.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Found no '{fileName}' in '{this.openTelemetryDistributionFolder}'. There is nothing " +
                    $"to gate, which means the distribution was never unpacked or does not contain a " +
                    $"native profiler for this platform.");
            }

            foreach (var library in shipped)
            {
                AssertLibraryExportsResolve(library);
            }
        });

    // Sources compiled ONLY on Windows, and therefore legitimately absent from CMakeLists.txt.
    // Every entry is `#if defined(_WIN32)`-guarded, so compiling it elsewhere would produce an empty
    // translation unit.
    //
    // An explicit list, not a heuristic: "has an early _WIN32 guard" does NOT separate the two sets.
    // clr_runtime_capture.cpp and stack_capturer.cpp are both guarded AND in CMakeLists.txt (they
    // have non-Windows code past the guard), so a guard-sniffing check would wave through a real
    // omission of either. Verified by testing that heuristic against the tree before rejecting it.
    //
    // If upstream adds a Windows-only source, this list is where it goes — and the assertion below
    // failing is how you find out that it needs to.
    private static readonly string[] WindowsOnlyNativeSources =
    [
        "native_symbol_resolver.cpp",
        "netfx_runtime_capture.cpp",
        "safe_native_walk_service.cpp",
        "stack_walk_guard.cpp",
        "thread_suspend.cpp",
    ];

    /// <summary>
    /// Asserts every vendored native source file on disk is listed in the build systems that must
    /// compile it, and that no source list references a file the vendored copy is missing.
    /// </summary>
    // The V2 failure mode, mechanized. A .cpp that exists on disk but is missing from a source list
    // is not a build error — the remaining translation units compile fine and link into a shared
    // library with an UNDEFINED SYMBOL. `ldd` still calls that library loadable. It fails only when
    // the CLR dlopen's it in a customer process.
    //
    // Three source lists, maintained independently, that diverge in different directions:
    //   CMakeLists.txt          Linux + macOS. Non-Windows only; excludes WindowsOnlyNativeSources.
    //   ...Native.vcxproj       Windows STATIC lib.
    //   ...Native.DLL.vcxproj   Windows SHARED lib — dllmain.cpp and interop.cpp live here, NOT in
    //                           the static one, so "is it in a vcxproj" must consider both.
    // The .vcxproj files enumerate sources EXPLICITLY, so an omission there is a Windows-only link
    // error that no other platform's build can surface. That has already happened once: line_probe.cpp
    // was added to CMakeLists.txt and not to the .vcxproj.
    //
    // BOTH directions are checked. Disk-not-in-list catches a file we added without wiring up;
    // list-not-on-disk catches a file upstream has that our vendored copy DROPPED — which is the
    // rebase accident this whole check exists for.
    //
    // Still necessary-but-not-sufficient: it cannot see a file upstream added that is in NEITHER our
    // copy nor our lists. Only the dlopen+dlsym gate catches that.
    private Target AssertNativeSourceListsAreComplete => _ => _
        .Executes(() =>
        {
            var cmakeLists = this.nativeProfilerProjectFolder / "CMakeLists.txt";
            var staticVcxproj = this.nativeProfilerProjectFolder / "OpenTelemetry.AutoInstrumentation.Native.vcxproj";
            var dllVcxproj = this.nativeProfilerProjectFolder / "OpenTelemetry.AutoInstrumentation.Native.DLL.vcxproj";

            foreach (var required in new[] { cmakeLists, staticVcxproj, dllVcxproj })
            {
                if (!required.FileExists())
                {
                    throw new InvalidOperationException(
                        $"Vendored native source is incomplete: '{required}' is missing. See " +
                        $"src/OpenTelemetry.AutoInstrumentation.Native/UPSTREAM-BASE.txt.");
                }
            }

            var cmakeContent = cmakeLists.ReadAllText();
            var windowsContent = staticVcxproj.ReadAllText() + dllVcxproj.ReadAllText();

            // Only the profiler's OWN top-level sources. lib/ holds dependencies vendored by UPSTREAM
            // whose few .cpp files CMake references by explicit relative path, not by this convention.
            var ownSources = this.nativeProfilerProjectFolder
                .GlobFiles("*.cpp")
                .Select(f => f.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (ownSources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Found no .cpp files in '{this.nativeProfilerProjectFolder}'. The vendored tree " +
                    $"is missing or was not copied correctly.");
            }

            var problems = new List<string>();

            foreach (var source in ownSources)
            {
                if (!windowsContent.Contains(source, StringComparison.Ordinal))
                {
                    problems.Add($"{source} -> on disk, in NO .vcxproj (breaks the Windows LINK only)");
                }

                if (WindowsOnlyNativeSources.Contains(source, StringComparer.Ordinal))
                {
                    continue;
                }

                if (!cmakeContent.Contains(source, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{source} -> on disk, not in CMakeLists.txt (breaks Linux and macOS). If it is " +
                        $"Windows-only, add it to {nameof(WindowsOnlyNativeSources)}.");
                }
            }

            // Reverse direction: a source list naming a file the vendored copy does not contain.
            var onDisk = ownSources.ToHashSet(StringComparer.Ordinal);
            foreach (var referenced in ExtractCMakeSourceNames(cmakeContent))
            {
                if (!onDisk.Contains(referenced))
                {
                    problems.Add(
                        $"{referenced} -> listed in CMakeLists.txt but NOT PRESENT in the vendored copy. " +
                        $"A file was dropped during vendoring or a rebase.");
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "Vendored native source lists are inconsistent:" + Environment.NewLine +
                    string.Join(Environment.NewLine, problems.Select(p => "  " + p)) + Environment.NewLine +
                    "This does not fail the build it breaks — it produces a library with an undefined " +
                    "symbol that only dlopen/LoadLibrary rejects.");
            }

            Serilog.Log.Information(
                "Native source lists consistent: {Total} own .cpp files ({WindowsOnly} Windows-only)",
                ownSources.Count,
                WindowsOnlyNativeSources.Length);
        });

    // Pulls bare `foo.cpp` entries out of CMakeLists.txt's add_library blocks. Deliberately ignores
    // anything containing a path separator: those are upstream's own vendored dependencies under lib/,
    // which this check does not own.
    private static IEnumerable<string> ExtractCMakeSourceNames(string cmakeContent)
    {
        return System.Text.RegularExpressions.Regex
            .Matches(cmakeContent, @"(?m)^\s*([A-Za-z0-9_]+\.cpp)\s*$")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);
    }
}

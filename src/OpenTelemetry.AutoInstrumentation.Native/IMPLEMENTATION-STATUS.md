# Line-level DI — implementation status

Tracks the native/build half of line-level dynamic instrumentation. Lives next to the vendored source
because that is what it describes; the customer-facing doc is `docs/dynamic-instrumentation.md`, which
covers function-level DI only.

**Every "done" below was verified by execution, not by inspection.** Where a claim rests on a measurement,
the measured value is quoted. Where something could not be verified, it says so and names what is missing.

## W2 — vendored native profiler and build integration

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 1 | Vendor upstream native source | DONE | 546 files, all byte-identical to fork tree; 0 missing in either direction |
| 2 | Record rebase baseline | DONE | `UPSTREAM-BASE.txt`; `git tag --points-at b61301e` = `v1.16.0`; distribution `VERSION` file independently reads `v1.16.0@b61301e...` |
| 3 | Confirm delta is only ours | DONE | `git diff b61301e` = 9 files, **+297/−1**, matching the recorded table |
| 4 | Source-list consistency gate | DONE | `AssertNativeSourceListsAreComplete`; 36 own .cpp (5 Windows-only) |
| 5 | Version-pin gate (V3) | DONE | `AssertManagedAssemblyVersionMatchesNativePin`; upstream ships `Version=1.0.0.0` |
| 6 | Wire gates into `Workflow` | DONE | Present in the executed target graph, ordered after unpack |
| 7 | Mutation-check the gates | DONE | 4/4 red — see "Mutation results" |
| 8 | Compile vendored source | DONE | Builds on macOS-arm64 and in-container linux-x64; `line_probe.cpp` compiles at 82% |
| 9 | Old-glibc build container | DONE | `docker/ubuntu1604-native.dockerfile` — glibc 2.23, cmake 3.20.5, g++ 9.4.0, clang 5.0.2 |
| 10 | Artifact sourcing (F2) | DONE | `ReplaceNativeProfilerInDistribution`; shipped `.so` md5 `d27a1763…` == our build, ≠ stock `698e00e1…` |
| 11 | Load gate on build output | DONE | `AssertNativeProfilerLoads` (`NativeLibrary.Load` + export resolution) |
| 12 | Load gate on shipped artifact | DONE | `AssertShippedNativeProfilerLoads` — separate target, see "Why two gates" |
| 13 | Conditional execution | DONE | Verified in BOTH states: skips with no binary, runs with one |
| 14 | CI job | WRITTEN, NOT RUN | `build-native-x64` in `main-build.yml`; YAML parses, `build` has `needs: build-native-x64`. **Never executed on a real runner.** |
| 15 | Live in-process verification | DONE | See "Live verification" |
| 16 | Other four RIDs | NOT STARTED | musl x64/arm64, glibc-arm64, Windows all still ship the stock binary |

### Mutation results — 4/4 red

Each gate was broken deliberately and confirmed to fail before being trusted green.

| Mutation | Gate output |
|---|---|
| Remove `line_probe.cpp` from `CMakeLists.txt` | `on disk, not in CMakeLists.txt (breaks Linux and macOS)` |
| Delete the file, keep it listed | `listed in CMakeLists.txt but NOT PRESENT in the vendored copy` |
| Drop `thread_suspend.cpp` from both `.vcxproj` | `on disk, in NO .vcxproj (breaks the Windows LINK only)` |
| Bump the pin to major 2 | `pin is stale … Update UpstreamAssemblyVersionMajor … to 1` |

The load gate was mutation-checked separately and is the strongest result here: with `line_probe.cpp`
removed from the source list, **the build succeeded (100%, exit 0) and `ldd` reported the library as
loadable**, while the gate rejected it with
`undefined symbol: _ZN5trace33LineProbeRejitHandlerModuleMethod22RemoveLineProbeRequestEi`.
So `ldd` would have shipped that binary. Note also that `AddLineProbes` was still *defined* in it — an
export-only check would have passed. Only the eager load catches this.

### Why two load gates, not one

`AssertNativeProfilerLoads` gates the **build output**; `AssertShippedNativeProfilerLoads` gates the
**artifact that ships**. The gap between them is real and was hit during development: the swap silently did
not run, the build output was correct, and the distribution still carried the stock binary — all green.
File-presence checks cannot catch it, because upstream's zip already contains both the `.so` and a
`.so.debug`. Only loading the shipped file and resolving `AddLineProbes` distinguishes the two. Confirmed
by restoring the stock binary and watching the shipped gate fail with
`these exports do not resolve: AddLineProbes, RemoveLineProbe`.

### glibc floor

The floor is fixed by the **build host's** glibc; no compiler flag changes it.

| Binary | Max GLIBC requirement |
|---|---|
| Built in ubuntu:16.04 (ours) | **GLIBC_2.18** |
| Upstream's shipped linux-x64 | GLIBC_2.18 |
| Upstream's shipped linux-arm64 | GLIBC_2.35 |
| Same source on a modern host | GLIBC_2.36 |

So for x64 the container **prevents a regression** rather than beating upstream. Verified loading on glibc
**2.23, 2.31, and 2.39**.

### pthread — a defect in upstream, not in our delta

`CMakeLists.txt` gains `find_package(Threads)` + `Threads::Threads`. glibc ≥ 2.34 folded libpthread into
libc, so upstream's modern build hosts emit no dependency — `readelf -d` on their shipped `.so` lists only
libm/libc/ld-linux. On glibc 2.23 libpthread is still separate, so the library builds and links clean and
fails at load with `undefined symbol: pthread_create`.

**Upstream's own shipped linux-x64 binary has this defect**: measured, it fails to load on glibc 2.23 under
both `RTLD_NOW` and `RTLD_LAZY`. `ldd -r` reported `pthread_create` as the only unresolved symbol. Our fix
makes our binary load where theirs does not. Planned for upstream contribution; see `UPSTREAM-BASE.txt` for
the rebase warning.

### Live verification

The sample app at `poc/live-e2e/SampleApp/` runs under the real CLR with `CORECLR_ENABLE_PROFILING=1` and
CLSID `{918728DD-259F-4A6A-AC2B-B85E1B658318}` (identical in `instrument.sh:160` and `dllmain.cpp:26`).

Profiler attach: `Initialize`, `ICorProfilerInfo12 found`, 42 modules, **0 errors**. This is the V3 pin
holding in practice rather than by string inspection.

With a stock-profiler control:

| | stock upstream | ours |
|---|---|---|
| `AddLineProbes` | `EntryPointNotFound` | returned normally |
| `RemoveLineProbe` | — | returned normally |

The native log shows every field surviving the 88-byte marshal:

```
AddLineProbes: received id: live-e2e:4242 from managed side with 1 line probes.
  * LineProbe Target: NoSuchAssembly | NoSuchType.NoSuchMethod(0) ilOffset=0 probeId=4242
    hoistedFieldToken=0 emissionMode=0 boxValue=0 callback=[...].DiLineIntegration.Probe
```

## W3 — managed callback

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 1 | `DiLineIntegration` (woven entry points) | DONE | 3 public static callbacks, signatures pinned by reflection |
| 2 | `DiLineIntegrationHelper` (logic) | DONE | Hot-path guards; 20 new tests |
| 3 | `ILineProbeSink` (test seam) | DONE | Lets the hot-path contract be tested without the native profiler |
| 4 | Mutation-check the locks | DONE | 5/5 red — see below |
| 5 | Wire a real sink to the snapshot layer | NOT STARTED | `Configure` has no production caller yet |
| 6 | End-to-end probe fire | BLOCKED on #5 | Needs a sink plus a PDB-resolved probe on real customer code |

Signatures, transcribed from `line_probe.cpp` rather than assumed. The native side emits a MemberRef against
a hardcoded signature blob and `DefineMemberRef` SUCCEEDS even when no managed method matches — the call then
binds to nothing at runtime, so none of this is compiler-checked:

| Member | Native signature blob | Managed signature | Mode |
|---|---|---|---|
| `Probe` | `VOID, I4` | `void Probe(int)` | `Legacy` |
| `CaptureLocal` | `VOID, I4, OBJECT` | `void CaptureLocal(int, object)` | `LocalCapture`, box modes, async-hoisted |
| `ShouldCapture` | `BOOLEAN, I4` | `bool ShouldCapture(int)` | `GatedBox` |

Two facts verified in the native source, not inherited from the function-level pattern:

- **Both the type AND the methods must be `public`.** Line-level differs from `DiIntegrationN`, whose
  callbacks are deliberately internal because the profiler binds them REFLECTIVELY. Line-level emits
  `CallMember(callbackMemberRef, is_virtual: false)` — a direct `call` placed inside the CUSTOMER's assembly,
  which cannot reach a non-public member. Same `MethodAccessException` class of bug as before, one level deeper.
- **All members are static.** `IMAGE_CEE_CS_CALLCONV_DEFAULT` with zero `HASTHIS` occurrences in
  `line_probe.cpp`, so the emitted `call` passes no instance.

### Mutation results — 5/5 red

| Mutation | Caught by |
|---|---|
| Type → `internal` | `Type_IsPublic_BecauseCustomerIlCallsItDirectly` |
| `CaptureLocal`'s `object` param → `int` | Compile error in the null-capture test |
| Rename `ShouldCapture` → `ShouldCaptureNow` | Compile error in 4 places |
| Remove hot-path `try/catch` | `Probe_WhenTheSinkThrows…` + `CaptureLocal_WhenTheSinkThrows…` |
| Gate fails OPEN (`return true`) | `ShouldCapture_WhenTheSinkThrows_ReturnsFalseRatherThanPropagating` |

The rename mutation is the notable one: **the product still built clean** with the woven callback renamed.
That is exactly the silent breakage these reflection tests exist to catch, and it is why the names live as
constants on `LineProbeTranslator` and are asserted against the reflected members.

### Hot-path contract

The callbacks run at an arbitrary interior IL offset, inside customer code, on the customer's thread, often
within their own `try`/loop. So:

- **Every path is swallowed.** An escaping exception does not merely lose a snapshot — it alters control flow
  at a point the customer's code never anticipated and can surface as an impossible exception from a line
  that cannot throw. No logging either; a logger call is itself arbitrary managed code on the hot path.
- **The gate fails CLOSED**, note the asymmetry: the capture callbacks swallow and continue because the work
  is already done, while the gate swallows and returns `false`, suppressing capture. Returning `true` on
  failure would run the capture path with an unknown gate state.
- **No sink configured is a cheap no-op**, which is what allows disabling capture without re-weaving.
- **`hasValue` is separate from `value`** so a captured local that IS null stays distinguishable from no
  capture at all.

## Known-unverified / blocked

1. **The full `Workflow` has never run green in one pass.** The native chain does succeed in CI order
   (`Replace`, `AssertShipped`, and the version pin all `Succeeded`), but `Clean` fails inside a Linux
   container over a macOS-built tree (NuGet restore graph), and skipping it makes `BuildInstallationScripts`
   fail on `File ... already exists`. Neither implicates the native changes. **A real CI run on a clean
   checkout is the only way to confirm.**
2. **`Compile` fails for an unrelated, pre-existing reason.** `OpenTelemetry.Instrumentation.AWS` needs
   `AWSSDK.Core [4.0.3.3, 5.0.0)` (commit `6f063df feat: AWS SDK V4 migration`) but this machine resolved
   `AWSSDK.Core/3.7.300` — only 3.7.x is cached locally. `git status` shows that directory untouched by this
   work. Needs a restore with network access.
3. **net9.0 tests cannot run locally** — only runtimes 8.0.25 and 10.0.5 are installed. net9.0 compiles;
   net8.0 and net10.0 both pass 308.
4. **arm64 old-glibc container does not exist.** `ubuntu1604-native.dockerfile` installs an x86_64 CMake and
   cannot build arm64 as written. Upstream has no equivalent either, which is why their arm64 floor is 2.35.

## Local reproduction note

On Apple Silicon, building the 16.04 image under QEMU fails: the `ca-certificates` post-install script
segfaults (`Segmentation fault (core dumped)`), which surfaces misleadingly in the log as a `libperl5.22`
unpack error — dpkg names the last package in the batch, not the one that crashed. Rosetta works:

```
colima start --profile adotnative --arch aarch64 --vm-type vz --vz-rosetta
```

CI runs on x86_64 hardware and is unaffected.

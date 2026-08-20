# Line-level DI — implementation status

Tracks line-level dynamic instrumentation. Lives next to the vendored source because that is where the
mechanism lives; the customer-facing doc is `docs/dynamic-instrumentation.md`, which now documents
line-level probes as well as function-level (see its "Line-level probes" section and the four line-level
troubleshooting entries).

**Every "done" below was verified by execution, not by inspection.** Where a claim rests on a measurement,
the measured value is quoted. Where something could not be verified, it says so and names what is missing.

Last updated 2026-08-20, after merging `origin/main` (#439 output layer, #443 ServiceEvents, #445 CloudWatch
plugin) into the line-level branch.

## W2 — vendored native profiler and build integration

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 1 | Vendor upstream native source | DONE | 544 upstream files present, 0 missing in either direction; re-verified 2026-08-20 against a sparse checkout of `b61301e` |
| 2 | Record rebase baseline | DONE | `UPSTREAM-BASE.txt`; `git tag --points-at b61301e` = `v1.16.0`; distribution `VERSION` file independently reads `v1.16.0@b61301e...` |
| 3 | Confirm delta is only ours | DONE | measured 2026-08-20: 9 modified files **+275/−1**, plus `line_probe.{h,cpp}` (231+641). The +297/−1 previously recorded here was the 2026-08-03 figure and had drifted; `UPSTREAM-BASE.txt` now carries the reproduce command |
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
| 14 | CI job | WRITTEN, NOT RUN | `build-native-x64` in `main-build.yml`; YAML parses, `build` has `needs: build-native-x64`. **Never executed on a real runner** — the branch is still local, so nothing has pushed to trigger it. |
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

A sample app run under the real CLR with `CORECLR_ENABLE_PROFILING=1` and CLSID
`{918728DD-259F-4A6A-AC2B-B85E1B658318}` (identical in `instrument.sh:160` and `dllmain.cpp:26`).
NOTE: this was recorded from a `poc/live-e2e/` tree that no longer exists in the repo — the result stands
but is not re-runnable from a fresh checkout, which is one reason the W6 harness matters.

Profiler attach: `Initialize`, `ICorProfilerInfo12 found`, 42 modules, **0 errors**. This is the V3 pin
holding in practice rather than by string inspection.

With a stock-profiler control:

| | stock upstream | ours |
|---|---|---|
| `AddLineProbes` | `EntryPointNotFound` | returned normally |
| `RemoveLineProbe` | — | returned normally |

The native log shows every field surviving the marshal. NOTE the struct was **88 bytes** at the time of
this capture and is now **104 bytes / 16 fields** — `localTypeName` and `localIsValueType` were added for
non-int local capture. Offsets were re-measured as identical on both sides after that change:

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
| 5 | Wire a real sink to the snapshot layer | DONE | `DynamicInstrumentationManager.Initialize` constructs `LineProbeSink` and calls `DiLineIntegrationHelper.Configure` (`DynamicInstrumentationManager.cs:121`); `Cleanup` passes `null` (`:531`) |
| 6 | End-to-end probe fire | DONE | See "W4 — managed resolution engine" and "W6 — end-to-end evidence" |

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

## W4 — managed resolution engine

2,523 lines across 16 files in `src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/Instrumentation/LineLevel/`,
covered by 116 tests in the mirrored test directory.

| # | Item | Status | Evidence |
|---|------|--------|----------|
| 1 | `PdbReader` — sequence points, scopes, hoisted fields | DONE | 924 lines; portable + embedded PDB; refuses a PDB whose module GUID does not match the loaded assembly |
| 2 | `IlBoundaryScanner` — injection offsets | DONE | Reads the effect of the probed line at the NEXT sequence point, so the offset is a real instruction boundary |
| 3 | Merge-point rule (R-E) | DONE, narrowed by decision | `IlScanResult.IsReachableWithoutExecuting`; refuses a candidate when any branch before the line targets `(lineOffset, candidate]` |
| 4 | `LineProbeTranslator` — resolve and apply | DONE | 397 lines; one batched `AddLineProbes` per config; partial-succeeds on a bad local name |
| 5 | Multi-local capture | DONE | One probe per named local at the SAME offset, cap `MaxLocalsPerLine = 5`; needed ZERO native changes (`AddLineProbeRequest` dedups by (offset, probeId)) |
| 6 | `LineProbeSink` — probeId attribution | DONE | `List<int>` per key, so removing a config drops EVERY probe it owns |
| 7 | Non-int local capture | DONE | `localTypeName`/`localIsValueType` on the ABI; each value type boxed against its OWN corlib TypeRef, reference types not boxed |
| 8 | Async / iterator targets | DONE | Resolution follows `StateMachineAttribute` into `<Foo>d__N.MoveNext`, where the user's sequence points actually live; NO ABI change and NO wire-format change |
| 9 | Manager wiring and status taxonomy | DONE | `ApplyLineProbe` branch, retryable vs permanent, `RetireAppliedConfiguration` on in-place edits |
| 10 | Removal under load | DONE | Native `RemoveLineProbe` is best-effort (IL cannot be un-woven); dropping the sink registration is what guarantees no further capture |
| 11 | Native request list thread-safety | DONE | `std::mutex` in `line_probe.h`; `GetLineProbeRequests()` returns BY VALUE; `RemoveLineProbeRequest` returns a removed-count. Exercised 2026-08-20 by `R9RemovalUnderLoadE2E` (22/22) — removal from the config thread while user threads are mid-call, with an inverted negative control proving the silencing is causally real |

### Two silent-wrong-value bugs this engine had, both found by measurement

Recorded because neither produced an error, a crash, or a missing snapshot — only wrong data on the wire.

1. **Resolution read the field live at the injection offset, not at the requested line.** R-A reads at the NEXT
   boundary, which can be past a variable's scope, so probing the last statement of a block resolved to a
   *different* same-named variable in the following block. Fixed by requiring one field live at both offsets.
2. **The async path boxed every hoisted field as `System.Int32`.** Mutation-proven consequence: a `String`
   arrived as `System.Int32 = 2848328` — its heap pointer truncated to 32 bits — and a `Double` `32.5` arrived
   as `System.Int32 = 32`. Truncated, plausible, no crash, exported to the backend. Fixed by resolving each
   field's own token and skipping the box for reference types.

The counterpart failure mode is why resolution fails CLOSED: forcing a valid but wrong `System.Int32` token
onto every local crashed the application with
`TypeLoadException: Could not load type 'Invalid_Token.0x01000000'`. A wrong box token takes the CUSTOMER'S
METHOD down; it does not merely lose a snapshot.

## W5 — accepted limitations (decided, not outstanding)

| Limitation | Why it is accepted |
|---|---|
| The last statement of a method cannot be probed | The probe reads its line's effect at the next statement; there isn't one |
| In `Release`, the last statement inside an `if`/loop body, and `yield return` lines, cannot be probed | The merge-point narrowing (R-E). Precise per-region reachability needs a real basic-block CFG + dominance check, whose hard parts are EH regions, switch tables and `MoveNext` shapes. Deferred as a fast-follow: R-E fails CLOSED (a refusal naming the merge point, never wrong data) |
| Same probe behaves differently in `Debug` and `Release` | `Debug` emits a sequence point for a block's closing brace and `Release` does not. An operator trap, so it is documented in BOTH the limits list and the "not an executable statement" troubleshooting entry of `docs/dynamic-instrumentation.md` |
| Own `struct`s, `enum`s, `Nullable<T>`, generic/nested value types not capturable | Box token resolution is restricted to plain `System.*` value types and reference types |
| `ref` / pointer locals not capturable | `box` on a managed pointer is invalid IL |
| Constructors and types nested more than one level deep | Not resolved |

## W6 — end-to-end evidence

Every phase gated on a real-profiler capture, never on a green unit suite.

| Run | Result | Notes |
|---|---|---|
| Local, sync int local | 9/9 | `total` = 84/91/98 on ticks 12/13/14 (×7) — changing per-invocation values, so the probe read the real local rather than a constant or an unassigned default |
| Real beta control plane, non-int locals | 15/15 | `String` (no box), `DateTime`, `Double`, `Int32`; four line probes on four methods firing concurrently alongside two method-level probes |
| Real beta, multi-local | 17/17 | ONE config with `CaptureLocals:["count","label","weight"]` at one line produced three snapshots per hit, all under the same LocationHash; native log `wove probeId=1/2/3 at ilOffset=118` |
| Real beta, async + iterator | 19/19 local, 23 checks vs beta | Weave landed on `<ReserveAsync>d__5.MoveNext()` / `<DescribeAsync>d__6.MoveNext()`, never on the launcher; captured on the resumed continuation with the fraction intact (`ratio` 30/**32.5**/35) |

**The wire config for a line-level probe on an async method is IDENTICAL in shape to a sync one** — the
operator never has to know the method is async; the agent works it out from assembly metadata.

**Beta does not enforce the GA language gate.** `create-instrumentation-configuration` returns HTTP 200 and a
real ARN for `Language: "Dotnet"` WITH a `LineNumber`, round-trips it through `list` with `CaptureLocals`
preserved, and accepts a READY status report for it. So line-level .NET can be tested against the real
control plane today, without waiting on the enum rollout.

All of the earlier runs used a native profiler built on or before 2026-08-11, so none of them covered the
thread-safety fix from 2026-08-18. **RE-VERIFIED 2026-08-20, locally AND against real beta**, on the current binary
(`sha256 7896eaaf…`, byte-identical to `OpenTelemetryDistribution/osx-arm64/*.dylib`, carrying the
post-fix out-of-line `RequestCount` symbol):

| Harness | Result | What it covers |
|---|---|---|
| `DeployedAppDemo` (real agent, full lifecycle) | **19/19** | env-enable → poll → ReJIT weave → capture → OTLP export → status report; sync int, String/DateTime/Double, multi-local trio, async hoisted incl. non-int |
| `R9RemovalUnderLoadE2E` | **22/22** | 3 then 4 co-located probes, incremental add to an already-woven method, removal while user threads are in-flight, exact-count drop/double-fire window, full teardown to a pristine body, mixed emission modes |
| `R9` NEG-1 (`R9_NO_REMOVE=1`) | PASS | INVERTED control: with removal skipped the removed probe STILL fires, so the positive run's silencing is causally real rather than vacuous |
| `R9` NEG-2 (`R9_BAD_OFFSETS=1`) | 3/3 | mid-instruction (operand-byte) offsets are refused, nothing fires, body intact — and it self-checks that the offsets really were mid-instruction |
| `G1BranchEhE2E` | **10/10** | interior injection into real try/catch and branches — the highest remaining risk carried since the Phase-2 PoC |
| `N2MultiProbeE2E` | 5/5 | N probes at N offsets in one method |
| `AsyncLineProbeE2E` | 4/4 | hoisted local read across an `await`, from inside `MoveNext` |
| `LineProbeE2E` | 3/3 | the original single-probe weave-and-fire |
| `LineProbeGatedE2E` | 7/7 | `ShouldCapture` gate resolution and per-probe gating |
| `LineProbeTimingE2E` | no assertions | timing/stop-the-world measurement only. Its embedded July "N2" block is STALE (hardcoded offsets; reported `distinct fired = 0` and then concluded "single-probe-per-method"). Nothing fired, so that verdict is meaningless and is contradicted by `N2MultiProbeE2E` and `R9` above. Do not cite it |

That is 73 passing checks plus the two negative controls, on the current native code.

### Re-verified against REAL beta, 2026-08-20

`DI_BETA_ENDPOINT=https://application-signals-beta.us-west-2.api.aws`, run from the operator's terminal (this
leg needs credentials CI does not have). Every config-API call returned HTTP 200; each snapshot below is
tagged with **beta's own** `LocationHash`, so the loop closed through real AWS rather than the local mock.

| Config / LocationHash | Line | Captured | Type |
|---|---|---|---|
| `Reserve` `2dfa7964ec88e050` | 145 | `total` 77 | Int32 |
| `DescribeNote` `f6879fb3a9027ef7` | 169 | `note` "item-11", "item-14" | String, no box |
| `DescribeStamp` `2336de64e18d1042` | 176 | `stamp` 1/12/2026, 1/15/2026 | DateTime |
| `DescribeRatio` `24c5024385ef63d6` | 183 | `ratio` 16.5, 21 | Double |
| `DescribeAll` `1c3f4de6e937129e` | 200 | `count` 33/42, `label` all-11/all-14, `weight` 2.75/3.5 | ONE config, three locals, one line |
| `ReserveAsync` `b87bdbf8cb494230` | 226 | `total` 99, 126 | Int32, hoisted |
| `DescribeAsync` `ae072e2bbe2c2aa1` | 251 | `note` "async-item-14", `count` 154, `ratio` **27.5 / 30 / 32.5 / 35** | ONE config, three hoisted locals |

The async `ratio` series is the load-bearing one: 27.5 and 32.5 are exactly what the boxed-as-`Int32` defect
destroyed (it would yield 27 and 32 — truncated, plausible, no error). Asserting only on the i=14 sample would
have proved nothing, because 35 is integral.

Also confirmed on this run: beta accepted `Language: "Dotnet"` WITH a `LineNumber` on all seven line configs
and returned real ARNs; `CaptureLocals` round-tripped through `list`; eight configs reported READY with
`UnprocessedStatusEvents: []`. The GA language gate is still not enforced in beta.

Two things this run also showed, neither a line-level defect:

- Beta returns `SyncInterval` (300 for PROBE, 60 for BREAKPOINT) and the agent **ignores it** — the TODO at
  `DynamicInstrumentationClient.cs:229`, now confirmed against the real backend rather than inferred.
- `create` on the method-level `OrderService.Process` config returned **409** ("already exists for this
  service, environment, signalType and location"). Expected: beta persists configs ~24 h and a config with no
  line number is stable across source edits, so it survives from a previous run.

CAVEAT ON WHAT WAS CAPTURED: the harness's aggregate `STEP 4` tally for this run was not preserved (it goes to
stdout, not to `logs/mock-backend.out`, and scrolled away). The evidence above is read directly off the wire
log, which is the primary source; the pass count is not independently recorded.

A PRIOR RUN THE SAME DAY FAILED 20 CHECKS ON EXPIRED CREDENTIALS, not a regression. The tell is
`[BETA-CREDS] using default chain` (rather than `using AWS_* env vars`) plus every call returning
`403 "The security token included in the request is invalid"`. With no configs delivered nothing weaves and
every downstream check cascades. Four checks "passed" in that run, all vacuously — see the empty-set defect
below.

### A vacuous check found and fixed in the harness

`every Reserve line config points at the CURRENT source line` reported **PASS with "saw: none"** on the
expired-credential run. `LINES_CURRENT` was seeded from `BOUNDARY_OK` and the comparison loop only ever
downgrades, so an empty result set skipped the body and left it green. That is the one assertion whose whole
purpose is catching a stale absolute line number, and it could not fail when nothing was fetched. Now guarded
on an empty set; logic exercised across all three branches (empty → FAIL, correct-only → PASS, stale-mixed →
FAIL). Note the guard itself has NOT run inside a real beta run — that block only executes in beta mode.

### Four harness defects found while re-verifying — all staleness, none a product defect

Recorded because each one initially LOOKED like a product regression, and three of the four were silent.

1. **All seven harnesses pointed at a deleted path.** They computed `REPO_ROOT` from their old in-repo home
   (`<repo>/poc/<Harness>/`) and loaded `poc/fork/otel-dotnet-fork/.../*.dylib`, which no longer exists now the
   profiler is vendored in-tree. They fail loudly, so this one was cheap. Now they honour `DI_REPO_ROOT` /
   `DI_PROFILER_OVERRIDE` and default to the in-tree build.
2. **Stale 14-field interop struct → SIGBUS.** Each harness hand-rolls its own `NativeLineProbeDefinition`, and
   they predate the `localTypeName`/`localIsValueType` fields (88 → 104 bytes). The native side read
   `localTypeName` as a pointer past the end of the struct: `EXC_BAD_ACCESS / SIGBUS`,
   `KERN_PROTECTION_FAILURE at 0x0000000a00000002`, inside `CorProfiler::AddLineProbes`.
3. **`LocalIsValueType = 0` on an int local → `InvalidProgramException`.** Zero suppresses the `box` entirely,
   but the callback is `void CaptureLocal(int, object)`, so handing it a raw int32 is invalid IL and the CLR
   rejects the woven method. Must be 1 for a value-type local.
4. **`AsyncLineProbeE2E` hardcoded the wrong hoisted-field token.** Its default `0x04000013` is
   `<>t__builder` (an `AsyncTaskMethodBuilder<int>`), not `y`; the probe read the builder's first four bytes and
   boxed them as Int32, giving `930373888` instead of `42`. Resolved by reflecting the built assembly:
   `<y>5__1` is `0x04000015`. **This is a property of the spike, not the product** — the product resolves
   hoisted fields by NAME via `PdbReader` plus the `StateMachineHoistedLocalScopes` CDI, which is precisely
   why it survives a recompile that renumbers metadata. Default now pinned, with that reasoning in a comment.

The general lesson, and the reason these harnesses are a liability as well as an asset: **a hand-rolled copy of
a marshaled struct is a second source of truth that no compiler checks.** Defects 2–4 all produced either a
crash or a plausible wrong number with no error, and defect 4 is the exact failure mode the product's
fail-closed token resolution exists to prevent.

**A pipeline hides the exit code.** `bash run.sh | tail -45` reports `tail`'s status, so a SIGBUS crash came
back as exit 0 with output that merely looked truncated. Always capture to a file and read `$?` directly.

### Harness lessons that cost a debug cycle each

- **`nm … | grep -q` under `set -o pipefail` returns 141 (SIGPIPE).** grep exits on first match, `nm` dies, the
  pipeline "fails" — the AddLineProbes export gate reported MISSING for a binary that has it. Capture to a
  variable first, then match.
- **Configs must be created AFTER the target methods are JIT-compiled.** `RequestReJIT` only rewrites methods
  that already have JIT'd code; uncalled ones are skipped while the profiler still logs
  `Request ReJIT done for N methods`. The manager latches the applied key, so the skip is never retried. DI's
  first poll runs from the startup hook, before `Main`, so create-then-start loses the race. This broke
  method-level too.
- **AWS resource detectors block ~76 s off-machine** before `Main` runs. Kill with bare
  `RESOURCE_DETECTORS_ENABLED=false` — no `OTEL_` prefix.
- **Never mark a boundary by appending to a log another process owns.** The mock holds its own non-append fd
  and writes at its own tracked offset; its next flush overwrote the marker, `sed` matched nothing, and the
  checks silently fell back to the whole log — a broken scope became a confident wrong verdict.
- **Scope E2E assertions to a specific line/LocationHash.** An async `Double` check passed under a deliberate
  revert because an unrelated sync probe also captured a `Double` elsewhere.
- **A line-level config stores an ABSOLUTE line number and beta persists configs ~24 h**, so any source edit
  leaves yesterday's probe pointing at a comment and reporting ERROR forever. Cleanup must run BEFORE the app
  launches, because the first poll fires from the startup hook.

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
   net8.0 and net10.0 both pass **406 of 407** (the one skip is a function-level parity gap, not line-level:
   `ConfigurationPollerTests.StalenessWarning_ForcesFullResync_WhenNoSuccessWithinWindow`, whose feature is
   still only a TODO at `ConfigurationPoller.cs:42`). Confirmed in Debug AND Release — an earlier round had
   two tests hardcoding Debug-only IL offsets, so Release must be run separately.
4. **arm64 old-glibc container does not exist.** `ubuntu1604-native.dockerfile` installs an x86_64 CMake and
   cannot build arm64 as written. Upstream has no equivalent either, which is why their arm64 floor is 2.35.
5. **CLOSED 2026-08-20.** The mutex now has runtime coverage: `R9RemovalUnderLoadE2E` 22/22 against the
   current binary, including removal under load and a quiescent exact-count window that would catch a dropped
   or double-fired probe. Note this is macOS-arm64 only; no other RID has run it.
6. **Line-level has no contract test in CI.** The function-level suite (`test/contract-tests/tests/test/amazon/di/`)
   lives on the contract-tests branch and covers function-level only. Line-level is proven by an out-of-repo
   harness against real beta, which needs AWS credentials and therefore cannot run in CI.
7. **Only linux-glibc-x64 gets the forked profiler in CI.** `main-build.yml` passes `--build-native-profiler`
   for the Linux job only, so Windows, macOS and musl still ship the stock upstream binary. On those RIDs a
   line-level probe refuses with `ProfilerMissingLineProbeSupport` and function-level is unaffected — this is
   an OPEN SCOPE QUESTION for v1, not a settled decision.

## Not built out — function-level parity gaps

Listed here because they are absent features rather than line-level work, and a reader of this file will
otherwise assume the DI agent is complete.

| Gap | Where |
|---|---|
| Staleness tracking / forced full resync (no success for 30m probes / 5m breakpoints) | `ConfigurationPoller.cs:42` TODO; its test is the suite's only skip |
| Backend-supplied `SyncInterval` not wired into the poller | `DynamicInstrumentationClient.cs:229` TODO |
| Argument selection is positional, never by parameter name | `GetParameters()` is used for arity only; documented as a limitation in `docs/dynamic-instrumentation.md` |
| Client-side `AttributeFilters` (fleet-subset targeting) | absent; every config applies to every instance |
| Serialization wall-clock budget | `ValueSerializer` is bounded by depth/count/size but has no deadline |

## Local reproduction note

On Apple Silicon, building the 16.04 image under QEMU fails: the `ca-certificates` post-install script
segfaults (`Segmentation fault (core dumped)`), which surfaces misleadingly in the log as a `libperl5.22`
unpack error — dpkg names the last package in the batch, not the one that crashed. Rosetta works:

```
colima start --profile adotnative --arch aarch64 --vm-type vz --vz-rosetta
```

CI runs on x86_64 hardware and is unaffected.

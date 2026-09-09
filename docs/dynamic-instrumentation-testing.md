# Dynamic Instrumentation — testing infrastructure

How Dynamic Instrumentation (DI) is tested, what each layer does and does not prove, and how to run each
locally. Written for contributors; the customer-facing feature documentation is
[dynamic-instrumentation.md](./dynamic-instrumentation.md).

DI is harder to test than most of this repository, for one reason: **the thing being tested is a runtime code
rewriter.** A unit test can prove the managed agent decides correctly, but only a real instrumented process
can prove the rewrite actually happened and produced the right data. The layers below exist to close that gap
progressively.

---

## The layers

| Layer | What it proves | Where | In CI |
|---|---|---|---|
| **1. Unit** | The managed agent's decisions: config parsing, registry keying, capture policy, limits, status reporting | `test/AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests/` | ✅ every PR |
| **2. Contract** | A profiler-instrumented app really emits correct snapshots and reports status over HTTP | `test/contract-tests/tests/test/amazon/di/` | ⏳ lands with the DI contract-test PR |
| **3. Native gates** | The shipped native profiler loads, exports resolve, and its glibc floor is low enough | `build/Build.NativeProfiler.cs` + `pr-build.yml` | ✅ every PR |
| **4. Soak** | The managed capture path survives sustained load without leaking or corrupting | `…Tests/Soak/DISoakTests.cs` | ⚠️ runs, but only 5s by default |
| **5. Real-backend E2E** | The loop closes through the actual Application Signals backend | `test/di-harnesses/` | ❌ needs credentials CI does not have |

Layers 1 and 2 mirror the Java distro's two-layer model. Layer 3 is specific to .NET because .NET is the only
distro that vendors a native profiler.

---

## Layer 1 — unit tests

33 test files, **463 tests** (1 skipped), across `net8.0`, `net9.0` and `net10.0`.

```bash
dotnet test test/AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests/AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.csproj
# single framework, much faster:
dotnet test test/AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests/AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.csproj -f net8.0
```

The suite runs without a profiler. `DiIntegrationHelper` is driven directly, so `OnMethodBegin` →
`DIDataStore` → `OnMethodEnd` → drain is exercised end to end in-process. Line-level resolution is tested
against a translator seam rather than the real native profiler.

**The one skip is intentional and tracked:** `ConfigurationPollerTests.StalenessWarning_ForcesFullResync_…`
covers staleness tracking, which is not implemented (`ConfigurationPoller.cs` TODO).

Some tests mutate static state (`DIDataStore`, `AsyncLocal`, the registry) and are therefore serialized via
`[Collection("SerialProcessState")]`. If you add a test that touches static agent state, put it in that
collection or you will get intermittent cross-test failures.

**A note on runtime.** The suite should complete in roughly 20 seconds. If it takes minutes, something is
waiting on a socket — a test that starts a `TcpListener` and never answers the request will block until the
HTTP client's retry budget is exhausted. This has happened; the fix is for the fake server to return a
response, not to raise the timeout.

---

## Layer 2 — contract tests

Containerized end-to-end tests: a real instrumented .NET application, a mock DI configuration API, and a mock
OTLP collector. The test drives HTTP requests at the app and asserts on the snapshots the collector receives.

> **Status:** this layer is delivered by a separate pull request and is not yet on every branch. The paths and
> commands below describe it as it lands. Line-level contract tests are a follow-up on top of it — that suite
> was deliberately scoped to function-level first, because line-level did not exist when it was written.

```bash
cd test
bash ./build-and-install-distro.sh
bash ./set-up-contract-tests.sh DynamicInstrumentation.NetCore
pytest contract-tests/tests/test/amazon/di -v
```

Name the application explicitly. With no arguments, `set-up-contract-tests.sh` builds **every** contract-test
application — including ones that need build args and have their own job — for a run that only ever starts
one.

Components:

| Piece | Role |
|---|---|
| `images/applications/DynamicInstrumentation.NetCore/` | the instrumented app. `ProbeTargets.cs` carries `@probe:` markers so line numbers are derived, not hardcoded |
| `images/mock-di-api/` | stands in for the local CloudWatch Agent — serves probe configurations and receives status reports |
| `images/mock-collector/` | receives OTLP. DI snapshots arrive as **OTLP Logs**, so the collector must implement the Logs RPC, not just traces and metrics |
| `templates/di/*.json` | golden snapshot templates, compared structurally |

**Why the mock DI API matters:** it makes probe creation deterministic and offline. No AWS credentials, no
control-plane dependency, no waiting on a poll interval you do not control.

**`@probe:` markers, not line numbers.** The app's probe targets are located by searching for a marker comment
and resolving it to a line number at test time. Hardcoding line numbers means every edit to the sample app
silently retargets every probe. The resolver must **fail loudly** if a marker is missing or appears twice.

Two workflows run these:

- `di-contract-tests.yml` — DI suite only, path-filtered. Fast, targeted signal.
- `pr-build.yml`'s `contract-test` job — the whole contract directory, no path filter. **This is the merge
  gate**; the focused workflow is a convenience.

---

## Layer 3 — native profiler gates

The native profiler is the highest-risk artifact in the repository, and the ordinary build tells you almost
nothing about it. Four gates exist because each catches something the others cannot.

| Gate | Catches |
|---|---|
| `AssertNativeSourceListsAreComplete` | a source file added to `CMakeLists.txt` but not the `.vcxproj`, or vice versa |
| `AssertManagedAssemblyVersionMatchesNativePin` | the pinned `OTEL_AUTO_VERSION_MAJOR` drifting from the upstream assembly the profiler looks for |
| `AssertNativeProfilerLoads` | the built library failing to load, or a required export not resolving |
| `AssertShippedNativeProfilerLoads` | the swap into the distribution not happening, leaving the stock upstream binary in place |

**Why a successful build is not evidence.** A source file dropped from `CMakeLists.txt` still links on Linux.
A missing export still loads. `ldd` still calls the result loadable. Only an *eager* load with symbol
resolution rejects it.

This is mutation-verified, not assumed: renaming the exported `AddLineProbes` leaves `make` succeeding at
**100%** while `AssertNativeProfilerLoads` fails with *"loaded, but these exports do not resolve:
AddLineProbes."* That is the exact defect class the gate exists for — and because the managed side treats a
missing export as a normal runtime condition, without the gate line-level would ship silently dead.

```bash
bash build.sh CompileNativeProfiler
bash build.sh AssertNativeProfilerLoads --skip Clean Restore
```

### The glibc floor rule

**Always compile the native profiler in the old-glibc container. Never on the runner or your workstation.**

The minimum glibc a binary can run on is fixed by the glibc of the machine that **compiled** it — no compiler
flag changes this. Building on a modern host produces a technically-working binary with a silently raised
floor, which then fails to load anywhere older: customer hosts, and the test containers.

```bash
docker build -t adot-native-1604 -f ./docker/ubuntu1604-native.dockerfile ./docker
docker run --rm --mount type=bind,source="$PWD",target=/project -w /project adot-native-1604 \
  /bin/bash -c 'mkdir -p src/OpenTelemetry.AutoInstrumentation.Native/build && \
    cd src/OpenTelemetry.AutoInstrumentation.Native/build && \
    cmake ../ -DCMAKE_BUILD_TYPE=Release -DOTEL_AUTO_VERSION=1.16.0 \
      -DOTEL_AUTO_VERSION_MAJOR=1 -DOTEL_AUTO_VERSION_MINOR=16 -DOTEL_AUTO_VERSION_PATCH=0 && \
    cmake --build . --config Release --parallel'
```

ubuntu:16.04 gives glibc 2.23 and yields a floor of **GLIBC_2.18** — both values are printed by the
`build-native-x64` CI job on every run, so they are measured rather than assumed. Per the rationale recorded
in `main-build.yml`, that floor matches upstream's own `linux-x64` floor.

**A load check on the build host cannot detect a raised floor**, because the binary loads fine there. That is
why `build-native-x64` also runs a `dlopen` gate *inside* the old-glibc container:

```bash
LIB=src/OpenTelemetry.AutoInstrumentation.Native/build/bin/OpenTelemetry.AutoInstrumentation.Native.so
strings -a "$LIB" | grep -oE "GLIBC_2\.[0-9]+" | sort -uV | tail -1   # the floor
readelf -d "$LIB" | grep NEEDED
gcc -o /tmp/gate build/native-gate/dlopen_gate.c -ldl && /tmp/gate "$LIB"
```

This is what caught a missing `pthread` link that loaded on the runner and failed to `dlopen` on glibc 2.23.

`x64` only for now. The container installs an x86_64 CMake, so it cannot build arm64 as written.

---

## Layer 4 — soak

3 tests over the managed capture path, driven by `DI_SOAK_SECONDS` (default **5s**, so the CI run is a smoke
test rather than a soak).

```bash
DI_SOAK_SECONDS=1800 dotnet test --filter FullyQualifiedName~DISoakTests
```

**A native soak does not exist.** The gap is a duration-driven soak over ReJIT churn: `m_requests` growth
across add/remove cycles and `AllocHGlobal`/`FreeHGlobal` balance. Burst throughput is covered; *accumulation
over time* is not.

---

## Layer 5 — real-backend E2E

Manual, and **cannot run in CI** — it needs credentials for a non-public endpoint. It is the only layer that
proves the loop closes through the real control plane, including that the backend accepts the configuration
shape and that snapshots carry the backend's own `LocationHash`.

The harnesses live in [`test/di-harnesses/`](../test/di-harnesses/) — 20 projects covering the real-profiler
path, line-probe weaving, async hoisted locals, the refusal gates, removal under load, and the rejected
alternatives (hot reload, `ICorDebug` stop cost). See that directory's `README.md`.

Treat them as **drifting by default**. Nothing in CI forces them to track source changes, and staleness is
usually silent — a harness can keep passing while asserting on something that no longer exists. Several such
defects have already been found there. Re-verify before trusting a result.

---

## CI job map

| Job | Workflow | Covers |
|---|---|---|
| `build (ubuntu / windows / macos)` | `pr-build.yml` | compile + `dotnet test` across 3 TFMs |
| `build-native-x64` | `pr-build.yml` | native compile in old-glibc container, export resolution, glibc floor |
| `contract-test` | `pr-build.yml` | **the merge gate** — whole contract suite, no path filter |
| `di-contract-tests` | `di-contract-tests.yml` | DI suite only, path-filtered fast signal *(lands with the DI contract-test PR)* |
| `serviceevents-contract-test` | `serviceevents-contract-tests.yml` | ServiceEvents suite |
| `all-pr-checks-pass` | `pr-build.yml` | gate over every `pr-build.yml` job |

**Adding a job to `pr-build.yml` requires adding it to `all-pr-checks-pass`'s `needs` list.** The gate
enumerates every job in the workflow and fails with *"Jobs missing from needs array"* otherwise. It also treats
`skipped` as failure, so a gated job must not carry a `paths:` filter or a condition that can skip it.

---

## Practices worth keeping

**Prove a check fails when it should.** Break the thing deliberately and confirm the check goes red before
trusting it green. An assertion never observed failing may be asserting nothing. This repository has already
shipped one assertion that passed with the code it was asserting on deleted.

**Verify by content, not by presence.** A file existing, a directory being non-empty, or a target reporting
success proves nothing about *what* is in it. Compare bytes or symbols against a known-good and a known-bad
reference. `build/Build.cs` uses `FileExistsPolicy.Skip`, so a copy step will silently keep a **stale** DLL —
check `sha256`, and be aware there is a second copy under `test/dist/`.

**Build with `--no-incremental` before believing a clean result.** Warnings are errors here
(`TreatWarningsAsErrors=true`), and incremental staleness has masked real errors.

**Derive fixtures, do not hardcode them.** Line numbers from `@probe:` markers; method and type names from
reflection or the config. Hardcoded values decay into tests that pass against nothing.

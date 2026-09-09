# Dynamic Instrumentation — manual harnesses

Hand-run harnesses and spikes for Dynamic Instrumentation. **None of these run in CI**, and none of them
gate a merge. They exist to prove things the automated suites structurally cannot, and to preserve the
measurements behind design decisions recorded in
[`IMPLEMENTATION-STATUS.md`](../../src/OpenTelemetry.AutoInstrumentation.Native/IMPLEMENTATION-STATUS.md).

For the automated layers — unit, contract, native gates, soak — see
[dynamic-instrumentation-testing.md](../../docs/dynamic-instrumentation-testing.md).

## Why these still exist

The contract-test suite covers the cases that can be made deterministic and offline. Three things it cannot
cover:

1. **A real CLR profiler attached to a real process.** Contract tests run the shipped distribution, but these
   harnesses let you drive the profiler directly and observe the rewrite.
2. **The loop closing through the real backend.** That leg needs credentials CI does not have.
3. **Cost measurements.** Timing and load numbers need a quiet machine, not a shared runner.

## What is here

| Harness | Purpose |
|---|---|
| `ProfilerE2E`, `ProfilerDemo` | end-to-end against a real attached profiler |
| `LineProbeE2E`, `LineProbeGatedE2E`, `LineProbeTimingE2E`, `LineProbeSink` | line-probe weaving: basic, gated, timing, and the callback sink |
| `AsyncLineProbeE2E` | line probes in `async`/iterator state machines (hoisted locals) |
| `G1BranchEhE2E` | the branch-target and EH-clause refusal gates |
| `N2MultiProbeE2E` | several probes woven into one method body in a single pass |
| `W1WeaveStatusE2E` | per-probe weave outcomes surfacing as probe status |
| `R9RemovalUnderLoadE2E`, `R9SinkLib` | probe removal while the target is under sustained load |
| `CaptureLoadPhase3` | capture-path throughput |
| `MockBackendE2E` | agent against a local mock control plane |
| `DeployedAppDemo` | the full demo: instrumented app + `MockBackend` + collector. Has its own `RUNBOOK.md` and `DEMO-QA.md` |
| `ProbeBreakpointDemo`, `PipelineDemo` | narrower demos of one behaviour each |
| `CallTargetPoc` | upstream CallTarget behaviour, from before line-level existed |
| `HotReloadSpike`, `IcorDebugStopCost` | rejected-alternative spikes: hot reload, and the cost of `ICorDebug` stop-the-world |

`HotReloadSpike` and `IcorDebugStopCost` are kept deliberately. They are the measurements that ruled those
approaches out, and without them the choice of a ReJIT-based profiler looks arbitrary.

## Running them

Most are plain console projects:

```bash
dotnet run --project ProfilerE2E
```

`DeployedAppDemo` is script-driven — start with its `RUNBOOK.md`.

Two things you must supply yourself:

- **The collector binary.** `DeployedAppDemo/collector/` expects an `otelcol-contrib` binary, which is
  **not** committed (it is a ~334 MB third-party build). Download it from the
  opentelemetry-collector-contrib releases and drop it there; see that directory's `README.md`.
- **Credentials, for the live leg only.** Scripts referring to `$DI_BETA_ENDPOINT_URL` sign and forward the
  configuration-API leg to a non-public endpoint. The URL is deliberately not committed. Everything else runs
  fully local against `MockBackend`.

## Treat results here as needing re-verification

These are outside the automated suites, so nothing forces them to track source changes. Staleness in a
harness is usually **silent** — it can keep passing while asserting on something that no longer exists.
Several such defects have already been found here. Before trusting a result, confirm the harness still
exercises what its name claims.

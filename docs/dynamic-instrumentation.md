# Dynamic Instrumentation (.NET) — Function-Level Capabilities & Limitations

This document describes what the .NET Dynamic Instrumentation (DI) function-level capture engine
supports today, and — just as important — where its edges are. It is grounded in the current
implementation; each limitation names the code that enforces it.

Function-level DI lets an operator remotely configure a **probe** (capture at method entry/exit)
on a running .NET service with no redeploy. Configs are polled from the backend and woven into the
running process by the OpenTelemetry .NET AutoInstrumentation native CLR profiler via ReJIT.

> **Line-level DI is out of scope here.** Capturing at a specific source line inside a method body
> is a separate, later effort. Function-level is entry/exit only.

---

## What is captured

At entry and exit of an instrumented method, a snapshot may include:

- **Arguments** — subject to the `CaptureArguments` filter (see below).
- **Return value** — when `CaptureReturn` is enabled.
- **Exception** — type, message (truncated to `MaxStringLength`), and a filtered/capped stack trace.
- **Trace context** — `TraceId`/`SpanId` when an `Activity` is active.
- **Thread info** and **method duration**.

Values are serialized depth- and width-limited by `ValueSerializer` (see *Capture limits* below).

### Argument capture is positional (first-N), not select-by-name

`CaptureArguments` controls which arguments are captured (`DiIntegrationHelper.OnMethodBegin`):

- **Empty filter** → capture **every** argument, labeled positionally `arg0`, `arg1`, ….
- **Non-empty filter** (e.g. `["orderId", "quantity"]`) → capture the **first N arguments** where
  `N = filter length`, applying the filter entries as **labels** in order. Bounded by the actual
  argument count, so a filter longer than the argument list just captures what exists.

**Limitation — you cannot yet cherry-pick arbitrary parameters by name.** The filter is *positional*:
its entries rename the leading arguments; they are **not** matched against the method's real parameter
names. So on a method `Foo(a, b, c, d, e)` you can capture the first 2 (`a`, `b`) but you **cannot**
capture only `c` and `e`, or reorder. The woven callback receives arguments as a positional `object?[]`
with no parameter-name metadata, so name-based selection needs reflection over the target method — a
tracked follow-up (see the open review note on preferring real argument names). Until then:

- To capture a specific subset, it must be a **contiguous prefix** of the parameter list.
- The names you supply are cosmetic labels applied left-to-right, not a lookup key.

> **Consequence for wide methods:** even setting aside the parameter cap below, "capture only these 4 of
> 20" is only expressible today if those 4 are the first 4 parameters.

---

## Supported targets

| Target kind | Supported? | Enforced by |
|---|---|---|
| Instance methods | ✅ | `ProfilerTranslator` |
| Static methods | ✅ | `ProfilerTranslator` |
| **Async methods** (`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`) | ✅ (see below) | `DiIntegrationHelper.OnAsyncMethodEnd` |
| Overloaded methods (by parameter count) | ✅ with a caveat (see *Arity*) | `InstrumentationRegistry.IndexArities` |
| Constructors (`.ctor`) / static constructors (`.cctor`) | ❌ | `ProfilerTranslator.IsUnsupportedTarget` |
| Methods with `ref`/`out`/`in` by-ref parameters | ❌ (skipped by profiler) | native profiler `method_rewriter.cpp` |
| Static methods on a value type (`struct`) | ❌ (skipped by profiler) | native profiler `method_rewriter.cpp` |
| Generic **struct** instance methods | ❌ (skipped by profiler) | native profiler `method_rewriter.cpp` |
| Methods with **more than 9 parameters** | ❌ (see *Arity cap*) | `ProfilerTranslator.MaxSupportedParams` |

---

## Async methods

**Supported without a profiler fork.** For an `async` method the woven method returns an
*incomplete* `Task`/`ValueTask`. Serializing that task synchronously would capture an unfinished
result and could block or deadlock the caller (e.g. touching `Task.Result`).

The profiler's CallTarget mechanism already solves this: it **awaits** the returned task and then
calls a separate `OnAsyncMethodEnd` callback with the **completed, unwrapped result**
(`T` for `Task<T>`/`ValueTask<T>`; a null object for non-generic `Task`/`ValueTask`), or the fault
if the task threw. This is the profiler's built-in continuation
(`IntegrationMapper.CreateAsyncEndMethodDelegate` / `TaskContinuationGenerator`).

Accordingly, our capture engine:

1. **Defers on the synchronous end** for any awaitable return type — `DiIntegrationHelper.OnMethodEnd`
   returns early (capturing nothing, leaving the paired entry intact) when the return is
   `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`, matching the profiler's own
   `NoCodeIntegrationHelper.OnMethodEnd`.
2. **Captures on `OnAsyncMethodEnd`** — `DiIntegrationHelper.OnAsyncMethodEnd` records the completed,
   unwrapped return value (and any fault) exactly once.

**Limitation:** capture fires when the returned task **completes**, not at the C# `await` points
inside the method body. Locals across `await` boundaries are a line-level concern and are not
captured by function-level DI.

---

## Arity (parameter-count) cap

**Hard limit: 9 parameters. Not configurable.**

The engine ships ten fixed CallTarget integration types, `DiIntegration0` … `DiIntegration9`, one per
parameter count (`ProfilerTranslator.MaxSupportedParams = 9`). A method (or overload) with **more than
9 parameters is not instrumented**.

Why fixed, and why not configurable:
- Each `DiIntegrationN` is a distinct generic type the profiler binds by signature-array length. The
  set is compiled into the assembly; there is no runtime knob that would make the profiler accept an
  11th generic argument.
- Raising the cap would require either more generated integration types or switching to the profiler's
  `object[]` slow-path weave — a larger change deferred out of function-level v1.

**Behavior at the boundary (this is the reportable-vs-partial distinction):**

- If a method has **overloads at both supported and unsupported arities** (e.g. a 4-param and a
  12-param overload), the supported overloads are woven and the over-cap overloads are **silently
  skipped**. The apply result is `Applied` — a partial success, not an error.
- If **every** overload exceeds the cap, apply returns `NoSupportedArity` — a **permanent
  instrumentation failure** reported (PR3) as an `ERROR` status with cause `RUNTIME_ERROR`. The
  operator-facing message names the ">9 parameters" reason; the coarse backend enum has no
  arity-specific member.

> **"Capture only 4 of a method's 20 parameters" — can I?** No, not today, for two independent reasons:
> (1) a 20-parameter method exceeds the arity cap, so it is never woven at all (`NoSupportedArity`); and
> (2) even under the cap, `CaptureArguments` is positional (first-N), so a subset must be a leading prefix
> — see *Argument capture is positional* above. For a method with ≤9 parameters, capturing the first 4
> is fully supported.

---

## Overload disambiguation (same-arity collision)

The woven callback receives `(instance, args)` but **not** the method name or token. Co-located
methods are disambiguated by **parameter count** (`args.Length`), indexed at apply time by
`InstrumentationRegistry.IndexArities`.

**Limitation:** two instrumented methods on the **same type with the same parameter count** cannot be
told apart at capture time. This is a documented residual — a capture may be attributed to either
config sharing that `(type, arity)` bucket. (A future enhancement is an optional `Signature` field so
the profiler binds by exact parameter types.)

---

## Removal / uninstrumenting

**Removal is logical, not physical.** The native profiler exposes **no revert/remove export** (there
is no `RequestRevert`; the P/Invoke surface is `AddInstrumentations` only — see `NativeMethods.cs`).
A method whose IL was rewritten by ReJIT therefore **stays woven** for the life of the process.

When the backend removes a config, the SDK drops it from the `InstrumentationRegistry`
(`DynamicInstrumentationManager.OnConfigurationsChanged` → `RemoveStale`). On the next invocation the
still-woven `DiIntegration` callback finds no matching config (`registry.TryHit`/`Get` return
`false`/`null`), so `OnMethodBegin` short-circuits and **no snapshot is produced**. The residual cost
is the cheap woven prologue/epilogue that immediately returns.

**Implication:** removing a probe stops all captures for it immediately, but does not restore the
method's original (un-woven) IL until the process restarts.

---

## Capture limits (partial captures are *not* instrumentation errors)

Individual values are serialized within limits (`CaptureConfiguration`): `MaxStringLength`,
`MaxCollectionWidth`, `MaxCollectionDepth`, `MaxObjectDepth`, `MaxFieldsPerObject`, `MaxStackFrames`,
and (breakpoints) `MaxHits`.

When a value can't be fully serialized, the snapshot carries a per-value `NotCapturedReason`
(`Depth`, `FieldCount`, `CollectionSize`, `Timeout`, `AlreadyCaptured`). **This is a capture-level
partial, reported inside the snapshot — it is never an `ERROR` status on the configuration.** Only a
weave failure (see below) is an instrumentation `ERROR`.

- **Collections** are only walked when their size is known in O(1) (arrays, `ICollection<T>`,
  `IReadOnlyCollection<T>`, etc.); a countless `IEnumerable` is serialized as an object, not walked,
  so a lazy/infinite sequence is never enumerated on the user thread (`ValueSerializer`).

---

## Error-status taxonomy (instrumentation-failed vs capture-failed)

Two distinct failure channels — do not conflate them:

| Kind | Meaning | Where reported | Source |
|---|---|---|---|
| **Instrumentation failed** | The target could not be woven at all | `ERROR` status on the configuration | `InstrumentationApplyResult` + `MapErrorCause` |
| **Capture failed (partial)** | The method was woven and ran, but a value couldn't be fully serialized | `NotCapturedReason` inside the snapshot | `Capture.NotCapturedReason` |

Instrumentation-failed causes and their backend `ErrorCause` mapping
(`InstrumentationApplyResultExtensions.MapErrorCause`):

| Apply result | Reportable? | Backend `ErrorCause` | Meaning |
|---|---|---|---|
| `Applied` | no | — | Woven (possibly partial across arities). |
| `Skipped` | no | — | Intentionally not applied (unsupported target). |
| `TypeNotLoaded` | no (retried) | — | Target assembly not loaded yet; retried on a later poll, never reported (would spam every poll). |
| `MethodNotFound` | yes | `METHOD_NOT_FOUND` | Type resolved but has no method of that name. |
| `NoSupportedArity` | yes | `RUNTIME_ERROR` | Every overload exceeds the 9-parameter cap. |
| `RuntimeError` | yes | `RUNTIME_ERROR` | The native `AddInstrumentations` call threw. |

> **Status emission** (`StatusReporter.ReportError`) is wired in a later PR (PR3). The taxonomy and
> mapping above are in place now; the manager calls the hook at the point of failure
> (see the `TODO(PR3)` markers in `DynamicInstrumentationManager.OnConfigurationsChanged`).

---

## Runtime / platform scope

- **.NET 8, 9, 10** (`net8.0;net9.0;net10.0`). .NET Framework (net462) is not supported.
- Requires the AWS-distributed OpenTelemetry .NET AutoInstrumentation native profiler; DI is loaded
  as a plugin (`OTEL_DOTNET_AUTO_PLUGINS`).

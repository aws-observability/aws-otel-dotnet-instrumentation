# Dynamic Instrumentation (.NET)

Dynamic Instrumentation (DI) lets you capture data from a running .NET service — method arguments,
return values, exceptions, local variables, and timing — **without changing your code or redeploying**.
You define a **probe** from the CloudWatch console or API; the agent picks it up and starts capturing on
the next call. Captured data is exported as OTLP logs (snapshots) to CloudWatch.

Two kinds of probe are supported:

| | Where it captures | What it captures | Extra requirement |
|---|---|---|---|
| **Function-level** | Method **entry and exit** | Arguments, return value, exception, duration | — |
| **Line-level** | A specific **source line** inside a method body | Local variables in scope at that line | The target assembly's **PDB** must be deployed |

Line-level probes carry more restrictions than function-level ones — see
[Line-level probes](#line-level-probes) before creating one.

---

## Prerequisites

- **.NET 8, 9, or 10.** .NET Framework is not supported.
- Your application runs with the **AWS Distro for OpenTelemetry (ADOT) .NET auto-instrumentation**
  (the native profiler + plugin). DI ships as part of that distribution.
- **For line-level probes only: `linux-x64` (glibc).** Line-level needs a capability that is currently built
  only into the `linux-x64` profiler. On every other platform — Windows, `linux-arm64`, and Alpine/musl — a
  line-level probe reports an error and captures nothing, while **function-level probes work normally**. See
  [the troubleshooting entry](#line-level-probes-error-with-the-native-profiler-does-not-export-addlineprobes)
  for how this surfaces. Support for the remaining platforms follows.
- **For line-level probes only:** the target assembly's debug info must be deployed with it, as either a
  sidecar `.pdb` next to the `.dll` or an embedded PDB, and it must come from the same build as the
  assembly. Without it, line-level probes report an error and capture nothing; function-level probes are
  unaffected.

---

## Enabling DI

DI is turned on entirely through environment variables — no code change to your app. Set these
alongside the standard ADOT auto-instrumentation variables:

| Variable | Default | Purpose |
|---|---|---|
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED` | `false` | Master switch. Set to `true` to enable DI. |
| `OTEL_AWS_OTLP_LOGS_ENDPOINT` | `http://localhost:4316/v1/logs` | Where captured snapshots are sent. The default is the local CloudWatch Agent's OTLP logs receiver — the same default the Java, Python and Node.js agents use — so it is usually correct. Must be an OTLP/**HTTP** endpoint including the `/v1/logs` path. |
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL` | `http://localhost:2000` | The local CloudWatch Agent that delivers your probe configurations. Usually the default is correct. |
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL` | `600` | Seconds between checks for new/changed probes (minimum 10). |
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL` | `60` | Seconds between checks for new/changed breakpoints (minimum 10). |

Your service name and environment come from the standard OpenTelemetry variables `OTEL_SERVICE_NAME`
and `OTEL_RESOURCE_ATTRIBUTES` (`deployment.environment.name=...`).

Once enabled, create a probe from the CloudWatch console or the Application Signals API, targeting a
method by its namespace, class, and method name — plus a source line for a line-level probe. The agent
applies it automatically — no restart needed.

---

## What gets captured (function-level)

At the entry and exit of a probed method, a snapshot may include:

- **Arguments** passed to the method.
- **Return value**, if enabled on the probe.
- **Exception** type, message, and stack trace, if the method threw.
- **Trace context** (trace and span IDs) when the call is part of an active trace.
- **Thread** and **method duration**.

Large or deeply nested values are captured up to configurable limits (string length, collection size,
nesting depth, field count). When a value is too large to capture fully, the snapshot includes the
part that fit plus a note about what was truncated — this is normal and does not disable the probe.

### Choosing which arguments to capture

You can capture **all** arguments, or name a subset. Naming a subset captures the **first N arguments**
(where N is how many names you provide) and applies your names as labels, left to right.

**Limitation:** argument selection is **positional**, not by parameter name. On a method
`Process(orderId, quantity, customer, region)` you can capture the first two, but you cannot capture
only `customer` and `region` while skipping the earlier ones. To capture a specific argument, include
everything up to and including it.

---

## Supported methods (function-level)

| Method kind | Supported |
|---|---|
| Instance methods | ✅ |
| Static methods | ✅ |
| Async methods (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`) | ✅ |
| Overloaded methods | ✅ (with a caveat — see below) |
| Constructors / static constructors | ❌ |
| Methods taking `ref` / `out` / `in` parameters | ❌ |
| Methods on `struct` value types | ❌ |
| Methods with **more than 9 parameters** | ❌ |

A few things to know:

- **Async methods** are captured when the returned task **completes** — the return value is the awaited
  result, and a faulted task is captured as an exception. Capture happens at method completion, not at
  individual `await` points inside the method.
- **Overloaded methods on the same class with the same number of parameters** cannot be told apart, so
  a probe on one may capture data from the other. Probes on overloads with *different* parameter counts
  work correctly. If a same-count ambiguity is detected, the affected probes are reported with an error
  status so you know to disambiguate.
- **Methods with more than 9 parameters** are not supported and the probe will report an error status.

The 9-parameter and same-count-overload limits above are **function-level only**. A line-level probe
identifies the method body by the line itself, so overloads are never ambiguous and the parameter count
does not matter.

---

## Line-level probes

A line-level probe fires when execution reaches a specific **source line** and captures the **local
variables** you name. Unlike a function-level probe it is a point observation: there is no duration, no
return value, and no exception.

Name the file line and, optionally, the locals to capture. Naming no locals is still useful — the
snapshot then records only that the line was reached.

A snapshot from a line-level probe may include:

- **Local variables** you named, under `captures.lines.<line>.locals`.
- **Trace context** (trace and span IDs) when the call is part of an active trace.
- **Thread**, and a stack trace if enabled on the probe.

The same truncation limits apply as for arguments (string length, collection size, nesting depth, field
count).

### Requirements and limits

**Where the line can be:**

| | Supported |
|---|---|
| Instance and static methods | ✅ |
| Async methods, iterators (`yield return`), async streams | ✅ |
| Overloaded methods | ✅ (the line identifies the body) |
| Constructors / static constructors | ❌ |
| Types nested more than one level deep | ❌ |

**What a captured local can be:**

| | Supported |
|---|---|
| Reference types (`string`, collections, your own classes) | ✅ |
| Plain `System.*` value types (`int`, `double`, `bool`, `DateTime`, `Guid`, …) | ✅ |
| Your own `struct`s and `enum`s, `Nullable<T>`, generic or nested value types | ❌ |
| `ref` / pointer locals | ❌ |

Additional behaviour to know about:

- **Up to 5 locals per line.** Extra names beyond the fifth are dropped; the rest are still captured.
  Each captured local adds a call on that line, so a line that runs millions of times pays for every one.
- **Partial success is normal.** If some named locals resolve and others do not, the probe still applies and
  captures the ones that did. The probe reports **READY**, not an error — it really is live — so a misspelled
  or out-of-scope name shows up as a local that is simply absent from the snapshot, with the dropped names
  named in the agent's own log rather than in the probe's status. Check the snapshot's `locals` against what
  you asked for if a value you expected is missing.
- **The last statement of a method cannot be probed.** The probe reads the effect of your line at the
  *next* statement, so a line with no following statement in the same body is refused.
- **A line whose next statement is a branch target cannot be probed.** In `Release` builds this most often
  means the **last statement inside an `if` or loop body**, and `yield return` lines. Firing there would
  also fire on paths that skipped your line, so the probe is refused with a reason rather than reporting
  something untrue. Probing an earlier line in the same block, or the statement after the block, works.

  **This depends on how your service was compiled, so a probe can behave differently in `Debug` and
  `Release`.** In `Debug` the compiler emits a sequence point for a block's closing brace, which gives the
  probe a safe place to read from, so the last statement inside a block resolves normally. `Release` has no
  such sequence point and the probe is refused. Validate probe locations against a build compiled the same
  way as the one you deploy — a location accepted in a local `Debug` run can be refused in production, and
  the error message names the merge point when that happens.
- **A local must be in scope where the probe reads it.** A local whose scope ends on the probed line is
  refused rather than captured as a different variable that happens to share its name.

### Choosing which locals to capture

Locals are selected **by name**, not positionally — the opposite of argument selection for function-level
probes. Names must match the source exactly, and only locals in scope at the probed line are eligible.

---

## Removing a probe

Deleting a probe **stops capturing immediately** — no more snapshots are produced for it. The method's
performance returns to effectively normal. The instrumentation is fully cleared when the process next
restarts.

---

## Troubleshooting

### Probes show ACTIVE but no snapshots appear

ACTIVE means the probe was hit and a snapshot was captured — it says nothing about whether the export
succeeded. Check the export path:

- **Is something listening on the endpoint?** Snapshots go to `OTEL_AWS_OTLP_LOGS_ENDPOINT`, which defaults
  to `http://localhost:4316/v1/logs`, the local CloudWatch Agent's OTLP logs receiver. If the agent isn't
  running, or isn't listening there, exports fail and the snapshots are lost.
- **Is the endpoint OTLP/HTTP, with the `/v1/logs` path?** The path is not appended for you, and the
  transport is fixed at **`http/protobuf`** — neither `OTEL_EXPORTER_OTLP_PROTOCOL` nor
  `OTEL_EXPORTER_OTLP_LOGS_PROTOCOL` affects snapshots, even though both do affect traces and metrics.
  Pointing this at a gRPC-only port (`:4317`) yields no snapshots.

To send them somewhere else, set the endpoint explicitly:

```bash
export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://localhost:4318/v1/logs"
```

Setting the variable to an empty value does **not** disable export — a blank value is treated as unset and
falls back to the default, matching the other agents.

### A line-level probe went READY and then reported an error

This is expected behaviour, not a glitch. Applying a line-level probe happens in two steps at two different
times: the agent resolves your line to a location and reports **READY**, and the profiler splices the capture
into the method later — the next time that method actually runs. If the profiler then declines the location,
the probe changes to **ERROR** on the following status report (up to a minute later).

So a probe on a method that is called rarely can sit at READY for a long time before either capturing or
reporting an error. Both outcomes are normal:

- **READY, then snapshots** — the location was accepted.
- **READY, then ERROR** — the profiler refused the location. The error cause distinguishes the two kinds:
  `LINE_NOT_EXECUTABLE` means the location itself cannot be used and you should move the probe;
  `RUNTIME_ERROR` means something else prevented it, and the agent's own log names the reason.
- **READY, and nothing else** — the method has not been called yet.

A probe capturing several locals can be **partly** applied, and the two ways that happens are reported
differently:

- A name that is **not in scope** at your line is dropped at resolution time. The probe stays **READY** and the
  dropped names appear only in the agent's log (see *Partial success is normal* above).
- A location the **profiler** refuses for one of your locals reports **ERROR**, even though the other locals
  are still captured — because that local can never be captured there, and the status is the only channel
  that reaches you.

Either way, compare the snapshot's `locals` against what you asked for.

### A line-level probe reports an error mentioning "no readable PDB"

The target assembly was deployed without its debug info. This is the most common line-level failure,
because release container images routinely exclude PDBs. Build with debug info and copy it alongside the
assembly:

```xml
<PropertyGroup>
  <DebugType>portable</DebugType>   <!-- or: embedded, which cannot go stale -->
  <DebugSymbols>true</DebugSymbols>
</PropertyGroup>
```

Only **portable** and **embedded** PDBs are read. A legacy Windows PDB is treated as no debug info at all.

The check happens once per assembly per process, so fixing the deployment requires a restart — the probe
will not recover on the next poll.

### A line-level probe reports "PDB does not belong to the loaded module"

The PDB and the assembly came from different builds. A mismatched PDB maps your line to the wrong place
and would produce a snapshot that looks valid but reports the wrong values, so it is rejected outright.
Redeploy the `.pdb` from the same build as the `.dll`, or switch to `<DebugType>embedded</DebugType>`,
which cannot drift.

### A line-level probe reports "line N is not an executable statement"

The line carries no code the compiler can stop on — a blank line, a comment, a closing brace, a field or
`using` declaration — or it is outside the method you named. Move the probe to a statement.

The same status also covers the two structural refusals described under
[Line-level probes](#line-level-probes): the **last statement of a method**, and a statement whose
following statement is reachable without your line having run (in `Release`, typically the last statement
inside an `if` or loop body). In both cases the error detail names the reason. Probing a line one
statement earlier, or the first statement after the block, resolves it.

If the same probe worked when you tried it locally, check how each build was compiled: the second refusal
happens in `Release` but not in `Debug`, because only `Debug` emits a sequence point for a block's closing
brace. This is a property of the deployed binary, not of the probe.

### Line-level probes error with "the native profiler does not export AddLineProbes"

Either the platform does not support line-level yet, or the app was started without the profiler environment
variables.

**Check the platform first.** Line-level currently requires **`linux-x64` (glibc)**; on Windows,
`linux-arm64`, or Alpine/musl this error is expected and is not a misconfiguration. Function-level probes are
unaffected on every platform, since they do not use that API.

On `linux-x64`, this means the process is running a profiler that predates line-level support, or none at all.
Reinstall the ADOT .NET distribution and relaunch through `instrument.sh` (or the `adot-launch` script). The
profiler cannot be swapped without a restart, so this is not retried.

### App exits immediately with a "Permission denied" filesystem error

The profiler writes diagnostic logs to `/var/log/opentelemetry` by default. If that path isn't writable
(common on macOS and non-root containers), the process aborts at startup before your app runs. Point the
log directory at a writable location:

```bash
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$HOME/.otel-dotnet-auto/logs"   # any writable dir
```

Setting `OTEL_DOTNET_AUTO_LOG_DIRECTORY` is the preferred fix, especially in containers or multi-tenant
hosts. As a local-development last resort you can instead create the default path with the right
ownership:

```bash
sudo mkdir -p /var/log/opentelemetry && sudo chown "$(whoami)" /var/log/opentelemetry
```

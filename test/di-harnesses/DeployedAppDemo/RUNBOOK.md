# .NET Dynamic Instrumentation — Deployed-App Demo Runbook

**Covers function-level AND line-level.** One command runs all four steps; the phases below appear in that
order in its output.

```bash
cd ~/Desktop/DI-DOTNET-CONTEXT/harnesses/DeployedAppDemo
DI_REPO_ROOT=<path-to-repo> \
RESOURCE_DETECTORS_ENABLED=false \
bash run-demo.sh
```

It pauses between phases so you can talk. `DEMO_NOPAUSE=1` runs straight through — **do one warm-up run that
way before presenting**, so the first build and the distribution restore are already done.

Prereqs: `dotnet` SDK, and `OpenTelemetryDistribution/` present at `DI_REPO_ROOT`. **No AWS creds and no
network** — the backend is an in-process mock speaking the real wire shape. Self-cleaning.

> The harness lives outside the repo (`poc/` was deleted). `DI_REPO_ROOT` is what points it at the repo, and
> the profiler it loads comes from `OpenTelemetryDistribution/osx-arm64/*.dylib` — so if you rebuilt the native
> profiler, **swap it into the distribution first** or you are silently demoing old native code:
> `./build.sh CompileNativeProfiler ReplaceNativeProfilerInDistribution AssertShippedNativeProfilerLoads`

**Exit code = number of failed checks.** A green run is `exit 0` and **19/19** PASS.

---

**OPENING — SAY (before you run it):** "This is the full .NET Dynamic Instrumentation lifecycle on an
*unmodified* application — how an operator creates a probe, how the agent picks it up, and proof the native
profiler instruments the running app and gets real values out of it. The app has zero DI code. Everything is
real: the real ADOT distribution, the real DI client, the real native CLR profiler, real OTLP export. The only
mock is the backend, and it speaks the exact wire shape production does."

---

## STEP 1 — The operator creates the configurations

**DOES:** starts the mock backend empty, then POSTs nine `create-instrumentation-configuration` requests and
prints the full request and response JSON for each: one **function-level** PROBE
(`OrderService.Process`), one **method-level** BREAKPOINT (`CheckoutService.Complete`), and **seven
line-level** BREAKPOINTs on `InventoryService`.

**SAY (the operator step):** "First, what an operator does from the CloudWatch console or CLI. Nothing here is
our code — it's the public API."

**SAY (point at a line-level REQUEST):** "This is the one that matters today. It's the same request shape as a
function-level probe with two additions: a `LineNumber`, and `CaptureLocals` naming the variables to read at
that line. That's the whole operator-facing surface for line-level."

**SAY (point at the RESPONSE):** "The backend hands back a server-generated LocationHash, an ARN, and
`CreatedAt` as a *number*, not a date-string — that's the real backend's shape, and a bug we found and fixed in
the client."

---

## STEP 2 — Enable DI on an unmodified app (100% environment variables)

**DOES:** launches a plain console app (`SampleApp`, no DI code) through the real ADOT distribution with DI
enabled purely by environment variables.

**SAY:** "Ordinary console app — no DI package, no capture code. DI is turned on entirely through environment
variables, exactly how a customer enables it. No redeploy, no code change, no restart to add probes later."

**SAY (point at the env list):** "`OTEL_DOTNET_AUTO_PLUGINS` registers our AWS plugin, which hosts DI.
`ENABLED=true` turns it on and `API_URL` points at the backend. That's the entire integration."

**If someone asks about the PDB:** "Line-level needs the target assembly's PDB deployed next to it — portable
or embedded. That's the one extra deployment requirement line-level has over function-level, and it's the most
common reason a line probe fails in a container image."

---

## STEP 3 — The agent polls, the profiler weaves, values arrive over OTLP

**DOES:** the agent polls `list-instrumentation-configurations` on its own timer; the mock prints each
exchange; the native profiler ReJIT-rewrites the app's methods; captured snapshots arrive over OTLP
`/v1/logs` and are printed as they land.

**SAY (point at the poll):** "The agent starts polling by itself. Just service, environment, type — 'what
probes do I have?' The backend returns exactly what the operator provisioned. Nothing is hardcoded."

**SAY (point at a `[SNAPSHOT-BODY]` with `"lines"`):** "And here is a local variable read out of a running
method that was never written to expose it. Notice the shape — `captures.lines.<line>.locals` — the line number
the operator asked for, and the variable by name."

---

## STEP 4 — Verification (19 checks, exit code = failures)

**SAY (set this up before the checks scroll):** "These aren't printed hopes — each one reads the native
profiler's own log file or the bytes that arrived over OTLP. The profiler's log is written by the C++ binary;
our managed code cannot fake it."

The checks worth calling out, in the order they appear:

| Check | What to say |
|---|---|
| `native profiler ReJIT-wove OrderService.Process` | "Function-level still works — line-level is additive, not a replacement." |
| `snapshot body captured the orderId argument + return value` | "Function-level captures arguments and the return value." |
| `native profiler performed a line-probe IL rewrite` | "The profiler's own log, confirming a *line* probe was welded into the body." |
| `captured local 'total' carried a correct non-zero value (i*7)` | "A **changing** value, checked against a formula. A constant or a default would pass a 'did we get something' check — this one wouldn't." |
| `captured a STRING local (reference type, no box)` | "Reference types are read without boxing." |
| `captured a DATETIME local` / `a DOUBLE local` | "Value types are boxed against their *own* type. Boxing a DateTime as an Int32 crashes the customer's method — we found that the hard way." |
| `ONE config captured all THREE locals (count/label/weight) at one line` | "One configuration, three locals, three probes at the same offset — and all three report the same source line." |
| `ASYNC line snapshot received` | "Now the hard one: async." |
| `async probe wove the state machine's MoveNext, not the launcher method` | "This is the whole async problem. Your `async` method compiles into a state machine; the code the operator sees doesn't exist as a method at runtime. The agent follows the compiler's attribute to `MoveNext` and reads the *hoisted* field. The operator never has to know any of that — **the wire config for an async line probe is identical to a sync one**." |
| `async DOUBLE hoisted field boxed as System.Double with its fraction intact` | "Fraction intact — the earlier bug boxed it as Int32 and silently truncated 32.5 to 32. Asserting on an integral sample would have proven nothing." |

**CLOSING — SAY:** "At GA we swap one API URL to the real AWS backend — identical wire shape, nothing else
changes. In fact this same harness can point its config leg at real beta today."

---

## Variants

| Command | What it shows |
|---|---|
| `DEMO_LINE_ONLY=1 … bash run-demo.sh` | **Line-level in isolation** — creates no method-level config, so nothing else can supply the callback AssemblyRef. 6/6. Use this if someone asks "how do you know line-level isn't riding on the function-level weave?" |
| `DI_BETA_ENDPOINT=$DI_BETA_ENDPOINT_URL … bash run-demo.sh` | SigV4-signs and forwards the **config API leg to real beta**; snapshots stay local. **Needs live creds in your shell.** |
| `DI_PROFILER_OVERRIDE=/path/to/…dylib` | Loads a specific profiler without rebuilding the distribution. |

### If you demo the beta leg

- Expect a **409** on the method-level `OrderService.Process` create — beta persists configs ~24 h and a config
  with no line number is stable across source edits, so it survives from the previous run. Harmless.
- **Expired credentials look exactly like a total regression**: every call returns
  `403 The security token included in the request is invalid`. The tell is `[BETA-CREDS] using default chain`
  instead of `using AWS_* env vars`. Check that line first before believing anything is broken.

---

## Q&A — pre-loaded answers

> **Fuller version in [DEMO-QA.md](./DEMO-QA.md)** — organised by who is asking (manager / teammate /
> sceptic), including the questions with awkward answers and a live-failure triage table. The short list below
> is the subset most likely to come up in the room.

- **"Is the backend real?"** → "Mocked here, and it speaks the identical wire shape. I can point the config leg
  at the *real* beta backend with one environment variable — we've run 15 line-level checks against it."
- **"Does this work on async?"** → "Yes, including iterators and async streams, and the operator's config is
  identical to a sync one. Check 16 proves we wove the state machine rather than the launcher."
- **"What can't it capture?"** → "Your own structs and enums, `Nullable<T>`, generic or nested value types, and
  `ref`/pointer locals. Plain `System.*` value types and any reference type work. It refuses with a reason
  rather than capturing something wrong."
- **"Which line can't I probe?"** → "The last statement of a method, and — in `Release` — the last statement
  inside an `if` or loop body, because there's no safe place to read the effect. It's refused with a reason
  naming the merge point. The same probe can behave differently in Debug and Release, so validate against a
  build compiled the way you deploy."
- **"What platforms?"** → "Line-level ships on **linux-x64** first. The other runtime identifiers carry
  upstream's profiler, where line-level reports a typed error per probe and function-level is completely
  unaffected — we tested that against upstream's actual released binary. The remaining platforms follow once
  this one is proven in production."
- **"How do I know a probe is really live?"** → "It reports READY when the location resolves, and if the
  profiler later refuses that location it changes to ERROR — because the rewrite happens the next time the
  method runs, which can be well after you created the probe. So READY-then-ERROR is a real sequence, not a
  glitch, and silence just means the method hasn't been called yet."
- **"What's the overhead?"** → "The capture is rate-limited per probe — 5 per second by default, plus your
  MaxHits. Each captured local adds one call on that line, so a line in a hot loop pays for every iteration;
  that's why the limit is 5 locals per line."
- **"Can I remove a probe?"** → "Deleting it stops capture immediately. The IL can't be un-woven, so what's
  left is a cheap call that resolves nothing; it's fully cleared on the next restart."

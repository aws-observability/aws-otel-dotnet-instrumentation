# .NET Dynamic Instrumentation — demo Q&A

Answers for the questions a manager or a teammate actually asks. Ordered by who asks them.

**Rule for using this doc:** anything marked ⚠️ is not verified. Say so out loud rather than asserting it — one
confident wrong answer costs more credibility than ten "I'd have to check."

---

## A. The questions a manager asks

### "What am I looking at?"

A .NET service running normally, with **no debugging code in it**, and an operator attaching a probe to a
specific line of that running service and reading a local variable out of it. No redeploy, no restart, no code
change. Then detaching it.

### "Why does this matter?"

The alternative today is: add a log line, build, review, deploy, wait for the bug to happen again. That is a
multi-day loop for a question like "what was the value of `total` when this failed." This turns it into
minutes, without shipping anything.

### "Is this real, or is it a mock?"

Real, with one exception, and the exception is worth stating plainly:

| Component | Real? |
|---|---|
| The application | real .NET app, **zero** DI references in its csproj |
| Enablement | real — environment variables only |
| Native CLR profiler | real, doing real ReJIT and real IL rewriting |
| Configuration API | **real AWS beta** — create/list/delete/report all HTTP 200, SigV4-signed |
| Snapshots | **real CloudWatch Logs** in a real account, via the same exporter the CloudWatch Agent uses |
| The one stand-in | a local proxy that plays the CloudWatch Agent's role of signing and forwarding config calls |

Verified in this account: 76 × `list` 200, 2 × `create` 200, 3 × `delete` 200, 0 errors — and snapshot bodies
readable in CloudWatch Logs with `aws logs tail`.

### "How would a customer actually use it?"

1. A bug appears only in production and the value they need was never logged.
2. In the CloudWatch console they pick the service, the method or the source line, and the locals they want.
3. The CloudWatch Agent already on the host serves that config; the agent inside the process picks it up and
   the profiler rewrites the method on its next call.
4. Snapshots arrive in CloudWatch Logs. They read the real values.
5. They delete the probe. Capture stops.

The console in this demo is a stand-in for the CloudWatch UI, not the product.

### "What does it cost the customer's application?"

Capture is rate-limited per probe (5/sec by default, plus a MaxHits budget), and each captured local adds one
call on that line — so a line in a hot loop pays per iteration. That is why the limit is 5 locals per line.
Values are clamped (255-char strings, 20 collection elements), so a probe cannot dump unbounded data.

### "Can this break a customer's service?"

The failure modes we found and fixed were the dangerous kind, and they are worth naming because it shows the
bar: boxing a `DateTime` against the wrong type token would corrupt the customer's method. That is now
asserted per type family (string, DateTime, double) in the demo, and the profiler refuses locations it cannot
safely read rather than guessing.

### "Is it ready to ship?"

The .NET **feature** is code-complete and verified against real beta. What is not finished is release
mechanics: CI has gaps, the branch is local-only, and line-level currently ships on **linux-x64 only**.
Be precise about that distinction — "the feature works, the pipeline isn't done."

### "How does this compare to the other languages?"

| | Java | .NET |
|---|---|---|
| Unit tests | 243 | **419** |
| Contract tests | 38 | 28 |
| Golden-file schema tests | 3 | 3 |
| E2E in CI | 1 | 0 |
| Soak | 0 | 0 (see §D) |

Ahead on unit coverage, behind on contract tests and CI E2E.

---

## B. The questions a teammate asks about the feature

### "How does line-level actually work?"

The agent resolves your file+line to an IL offset using the assembly's PDB, then hands that offset to the
native profiler, which ReJIT-rewrites the method body to call back into managed code at that point, passing
the named local. The local is read from its actual slot — not reconstructed.

### "Can I put a probe on ANY line?"

No, and the honest claim matters here: **anywhere a statement exists with the local in scope**. It refuses
with a reason otherwise. Four documented refusals:

- the line is not an executable statement (comment, brace, declaration)
- it is the **last statement of a method** — the probe reads at the *next* statement
- its next statement is a branch target (in `Release`, typically the last line of an `if`/loop body)
- the named local is not in scope at that line

Demo this deliberately: probe a good line (captures), then probe `return label;` (refused, with the reason).
"It works where it can and tells you exactly why where it can't" is a stronger claim than "it works
everywhere," which is false and invites a live counter-example.

### "Does it work on async methods?"

Yes, and the operator's configuration is **identical** to a sync one. Under the hood an `async` method compiles
into a state machine, so the code the operator sees does not exist as a method at runtime — the agent follows
the compiler's attribute to `MoveNext` and reads the *hoisted field*. The demo asserts we wove `MoveNext` and
not the launcher method, and that a `double`'s fraction survives intact.

Iterators (`yield return`) and async streams work too.

### "What can't it capture?"

Your own structs and enums, `Nullable<T>`, generic or nested value types, `ref`/pointer locals. Plain
`System.*` value types and any reference type work. Constructors are not supported, and types nested more
than one level deep are not.

### "Why linux-x64 only?"

Line-level needs `AddLineProbes`, an export that exists only in our build of the native profiler, and only
the linux-x64 job builds it today. Everywhere else ships upstream's binary, where a line-level probe reports a
typed error and **function-level is completely unaffected**. That was tested against upstream's actual
released binary, not assumed.

### "Why does it need a PDB?"

To map source line → IL offset. Portable or embedded PDBs both work; embedded cannot go stale. This is the
most common line-level failure in practice because release container images routinely strip PDBs.

### "How do I know a probe is really live?"

It reports READY when the location resolves, and changes to ERROR if the profiler later refuses that location
— because the rewrite happens the next time the method runs, which can be well after the probe was created.
So **READY-then-ERROR is a real sequence, not a glitch**, and silence just means the method has not been called.

⚠️ Do not put READY→ERROR on screen as a scripted step: it depends on when the CLR next ReJITs, and it did not
reproduce across two identical runs.

### "What happens when I delete a probe?"

Capture stops. Not instantly, though, and it is worth saying so: the backend forgets it immediately, but the
agent only learns on its next poll, so a few more captures can arrive in that window. Measured: 24 hits at
deletion, 34 after the next poll, then frozen. The IL cannot be un-woven, so what remains is a cheap call that
resolves nothing; fully cleared on the next restart.

### "Can I add and remove probes repeatedly?"

Yes — verified by re-adding the same probe after deleting it and seeing it capture again.

---

## C. The sceptical questions — the good ones

### "How do you know the profiler actually rewrote anything, rather than your test code faking it?"

The assertions read the **native profiler's own log file**, which is written by the C++ binary. Managed code
cannot forge it. The demo prints the relevant lines.

### "How do you know line-level isn't just riding on the function-level machinery?"

Run `DEMO_LINE_ONLY=1` (6/6). Its first check proves **no CallTarget weave happened at all** in that run, so
nothing else could have supplied the callback reference — then it shows the profiler defining that reference
itself and capturing a real value.

### "Could the captured value be a constant, or a default that happens to look right?"

Every value assertion is against a **formula**, not a fixed number: `total == i*7`, `amount == id*11`,
`doubled == i*9`. A constant or a zero would fail. The async double asserts the fraction survives, because an
integral sample would have hidden the truncation bug we actually had.

### "Has anything here ever been wrong?"

Yes, and this is the answer that builds trust rather than spending it. Examples from this work:

- An assertion that **passed with the code it was testing deleted** — found by mutation, then fixed.
- A boxing bug that would have crashed a customer's method on a non-`int` local.
- A packaging bug that silently omitted the DI DLL from the `.nupkg` while every E2E stayed green.
- A snapshot-schema conclusion I had **backwards** until I found the actual consumer's parser.

Every one was found by measuring rather than reasoning. That is the point.

### "What's the weakest part of this?"

CI, honestly. `pr-build.yml` has no native-profiler job, so a PR can break the C++ and go green; there is no
line-level contract test; and no E2E in CI. All tracked, none of it hidden.

---

## D. Questions that have awkward answers — say them anyway

### "Is there a soak test?"

**No.** There are three useful invariant tests in a directory named `Soak`, with a 5-second default window,
and only one of them genuinely needs wall-clock time. They measure nothing that accumulates — no memory, no
handles, no native registration counts — so raising the duration would not make them a soak. Java has none
either. It is a real gap, not a solved problem.

### "Are snapshots visible in the CloudWatch console?"

Yes — but via an OTel Collector we run, not the CloudWatch Agent. The **public** agent cannot ingest OTLP logs
at all (it has OTLP receivers for metrics and traces only), and the DI-capable agent build is not publicly
downloadable. The collector uses the same `awscloudwatchlogs` exporter the agent would.

⚠️ The log group name is our choice. The consumer takes it as a parameter, and no canonical DI group name
exists in any code we could find. Worth confirming with the agent/console team.

### "Why is the demo split across two accounts?"

The beta config API is **allowlisted by account** (`AWSPulseControlPlane` manages it via `Pulse.cfg`), so only
an allowlisted account can create probes. Snapshots are plain CloudWatch Logs and can go to any account. In
production this does not arise — a customer calls the API with their own credentials and snapshots land in
their own account.

### "What's left before GA?"

Release mechanics, not features: first real CI run, a native-profiler job in PR builds, a line-level contract
test, the remaining four runtime identifiers, and a native soak. The feature itself is verified.

---

## E. If something breaks live

| Symptom | First thing to check |
|---|---|
| Every config call 403s, `{"Message":null}` | credentials — expired, or an account not allowlisted. `aws sts get-caller-identity` |
| Probe created but never captures | is `CodeUnit` the **namespace**? A wrong one is accepted and silently matches nothing |
| DI never polls at all | `OTEL_DOTNET_AUTO_HOME` — without it the plugin never loads and DI never starts, with no error |
| Line probe refused | one of the four documented refusals in §B — read the reason, move the probe one statement |
| Snapshots missing from the console | check the **region and account** the collector wrote to; the banner prints both |
| App looks pre-instrumented | beta persists configs ~24h; delete them at the end of a session |
| Line-level reports "does not export AddLineProbes" | wrong platform, or the distribution has upstream's stock profiler |

**Rehearse once before presenting.** Both `run-demo.sh` and the two-terminal `run-live.sh` + `di-console.sh`
flow. Every failure in the table above cost real debugging time at least once.

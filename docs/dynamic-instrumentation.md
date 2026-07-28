# Dynamic Instrumentation (.NET)

Dynamic Instrumentation (DI) lets you capture data from a running .NET service — method arguments,
return values, exceptions, and timing — **without changing your code or redeploying**. You define a
**probe** on a method from the CloudWatch console or API; the agent picks it up and starts capturing on
the next call. Captured data is exported as OTLP logs (snapshots) to CloudWatch.

> This is **function-level** DI: capture happens at method **entry and exit**. Capturing at a specific
> source line inside a method body is not supported.

---

## Prerequisites

- **.NET 8, 9, or 10.** .NET Framework is not supported.
- Your application runs with the **AWS Distro for OpenTelemetry (ADOT) .NET auto-instrumentation**
  (the native profiler + plugin). DI ships as part of that distribution.

---

## Enabling DI

DI is turned on entirely through environment variables — no code change to your app. Set these
alongside the standard ADOT auto-instrumentation variables:

| Variable | Default | Purpose |
|---|---|---|
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED` | `false` | Master switch. Set to `true` to enable DI. |
| `OTEL_AWS_OTLP_LOGS_ENDPOINT` | *(unset)* | Where captured snapshots are sent — the local collector/agent's OTLP logs endpoint. **Required** to see any data (see below). |
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL` | `http://localhost:2000` | The local CloudWatch Agent that delivers your probe configurations. Usually the default is correct. |
| `OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL` | `600` | Seconds between checks for new/changed probes (minimum 10). |

Your service name and environment come from the standard OpenTelemetry variables `OTEL_SERVICE_NAME`
and `OTEL_RESOURCE_ATTRIBUTES` (`deployment.environment.name=...`).

Once enabled, create a probe from the CloudWatch console or the Application Signals API, targeting a
method by its namespace, class, and method name. The agent applies it automatically — no restart needed.

---

## What gets captured

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

## Supported methods

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

---

## Removing a probe

Deleting a probe **stops capturing immediately** — no more snapshots are produced for it. The method's
performance returns to effectively normal. The instrumentation is fully cleared when the process next
restarts.

---

## Troubleshooting

### Probes show ACTIVE but no snapshots appear

The most common cause is a missing **`OTEL_AWS_OTLP_LOGS_ENDPOINT`**. When it's unset, snapshots are
captured but have nowhere to go and are dropped. Set it to your local collector/agent's OTLP logs
receiver:

```bash
export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://localhost:4318/v1/logs"   # HTTP
# or, for gRPC:
export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://localhost:4317"
```

### App exits immediately with a "Permission denied" filesystem error

The profiler writes diagnostic logs to `/var/log/opentelemetry` by default. If that path isn't writable
(common on macOS and non-root containers), the process aborts at startup before your app runs. Point the
log directory at a writable location:

```bash
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$HOME/.otel-dotnet-auto/logs"   # any writable dir
```

Or create the default with the right ownership:

```bash
sudo mkdir -p /var/log/opentelemetry && sudo chown "$(whoami)" /var/log/opentelemetry
```

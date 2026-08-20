### Overview

MockDiApi stands in for the **Dynamic Instrumentation configuration API** that the DI agent polls. In
production that API is served by the local CloudWatch Agent; the agent fetches the probes an operator created
and reports each one's status back. Contract tests need both halves of that conversation, because with nothing
answering the poll the agent instruments nothing — and "no snapshots arrived" would then look identical to a
broken capture path.

Point the application at it with `OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL=http://di-api:2000`.

### Agent-facing API (shape must match production)

| Endpoint | Purpose |
| --- | --- |
| `POST /list-instrumentation-configurations` | Returns configurations matching the requested `InstrumentationType`. |
| `POST /report-instrumentation-configuration-status` | Records the status events the agent reports. |

Field names are **PascalCase**, matching the real wire contract. The response honours the real caching
protocol: it hands back a `SyncedAt` cursor and answers `Changed: false` when the agent echoes a current
cursor, so the unchanged path is genuinely exercised rather than papered over with a permanent
`Changed: true`.

### Test control API

| Endpoint | Purpose |
| --- | --- |
| `POST /_test/configurations` | Seed the configurations to serve: `{"Configurations": [ ... ]}`. |
| `GET /_test/status-reports` | Every status event the agent has reported, for assertions. |
| `POST /_test/reset` | Drop configurations and recorded statuses. |
| `GET /_test/health` | Readiness probe. |

Configurations may also be seeded at startup with the `DI_CONFIGS` environment variable (a JSON array), so a
test can be driven entirely through container env vars.

The `/_test` prefix keeps the control surface unmistakably separate from the real contract.

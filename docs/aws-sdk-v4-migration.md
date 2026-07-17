# AWS SDK for .NET v3 → v4 Migration

Tracking doc for [apm-telegen-3160](https://sim.amazon.com/issues/apm-telegen-3160) /
[aws-observability/aws-otel-dotnet-instrumentation#415](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/415).

## Goal & high-level decision

AWS SDK for .NET v3 reached EOL on 2026-06-01. We must support v4. v3 and v4 are **not
binary-compatible** and cannot coexist in one compiled assembly, so we match upstream and
**drop v3 / add v4** (upstream did this in
[opentelemetry-dotnet-contrib#2720](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/pull/2720),
labeled BREAKING).

**We keep our vendored fork of `OpenTelemetry.Instrumentation.AWS` and port v4 into it** rather
than depending on upstream's NuGet package. .NET/NuGet has no "patch upstream" mechanism — the
choice is consume-as-is or vendor-and-own. Our fork diverges substantially (owns span creation,
adds Kinesis/Lambda/Secrets Manager/Step Functions, adds ~415 lines of Bedrock GenAI body
parsing, captures Application Signals credential attributes), so depending on upstream directly
would lose all of that. Upstream is used only as a **reference** for the mechanical parts.

## Current status

| Phase | Status |
|---|---|
| Research: PR #2720 + public v3→v4 API docs | ✅ Done (see API map below) |
| Research: test-coverage gap analysis | ✅ Done |
| Contract-test enhancement (pre-migration, standalone) | ✅ PR [#433](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/433) — awaiting Docker suite run on v3 |
| Port vendored instrumentation to v4 | ⬜ Not started |
| Port SigV4 exporters to v4 | ⬜ Not started |
| csproj version + TFM bumps | ⬜ Not started |
| Remove v4-detection guard | ⬜ Not started |
| Build + tests green on v4 | ⬜ Not started |

Branch for migration work: `aws-sdk-v4-migration`.

## Sequencing (test-first)

1. **Lock in coverage on v3 first** — PR #433. A passing suite must genuinely mean "attributes
   preserved" before we touch the SDK version. Confirm green on the Docker contract-test suite.
2. **Port code to v4** — all in one atomic change (see note below).
3. **Re-run the same suite on v4** — now a passing run is a real migration sanity check.

> **Important:** the version bump and the code fixes are **one atomic change**, not a bump
> followed by fixes. The moment `AWSSDK.Core` goes to v4 the vendored source fails to compile
> (removed `IRequestContext.ImmutableCredentials` property; changed `AWS4Signer.Sign` signature).
> There is no intermediate state where the version is bumped and instrumentation still works.

## API migration map (v3 → v4)

Verified against `aws/aws-sdk-net@main` source, PR #2720, and the
[v4 migration guide](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/net-dg-v4.html).
Nearly everything survives; there are **two hard compile breaks**, both credential-related.

| v3 API we use | v4 status | Fix |
|---|---|---|
| `IRequestContext.ImmutableCredentials` (handler L108) | ❌ **Property removed** from the interface (the `ImmutableCredentials` *type* still exists). Interface now exposes `BaseIdentity Identity` and `AWSCredentials ExplicitAWSCredentials`. | Resolve via `requestContext.Identity as AWSCredentials` → `.GetCredentials()` (or `ExplicitAWSCredentials`), then read `AccessKey`. **Keep this block** (Application Signals cross-account support) — do not drop it like upstream did. |
| `AWS4Signer.Sign(req, cfg, metrics, ImmutableCredentials)` (exporters) | ❌ **4th param type changed** `ImmutableCredentials` → `BaseIdentity`. `ImmutableCredentials` is not a `BaseIdentity`, so current calls won't compile. | Pass the `AWSCredentials` object (from `FallbackCredentialsFactory.GetCredentials()`) to `Sign`; keep the resolved `ImmutableCredentials` only for the `x-amz-security-token` header. |
| `IRequest.DeterminedSigningRegion` (handler L109) | ✅ Still exists | No change |
| `ImmutableCredentials` type + `AccessKey`/`SecretKey`/`Token`/`UseToken` | ✅ Still exists (+ new `AccountId`) | No change |
| `DefaultRequest` ctor, `IRequest`, `SignatureVersion.SigV4` | ✅ Unchanged | No change |
| `FallbackCredentialsFactory.GetCredentials()` | ✅ Still exists, returns `AWSCredentials` | Keep the `AWSCredentials` handle to pass to `Sign` |
| `PipelineHandler` / `IRuntimePipelineCustomizer` / `RuntimePipeline` | ✅ No surface change | No change |
| SQS/SNS `MessageAttributes` | ⚠️ Defaults to `null` in v4 (was empty dict) | Our helpers already null-guard; optionally set `AWSConfigs.InitializeCollections = true` |
| `UseSigV4` on request | ❌ Removed | Not used by us |
| Request/response model value types | ⚠️ Now nullable (`bool?`, `int?`, …) | Reflection-based extraction (`GetValue`/`ToString`) is unaffected; watch any direct value-type casts |

## Files to change

### Vendored instrumentation — `src/OpenTelemetry.Instrumentation.AWS/`
- **`OpenTelemetry.Instrumentation.AWS.csproj`** — bump `AWSSDK.Core`/`SimpleNotificationService`/`SQS`/`XRay`
  from `3.7.300` to `[4.0.0, 5.0.0)` (upstream pins `AWSSDK.Core [4.0.3.3, 5.0.0)` for
  CVE-2026-22611); change min .NET Framework target `net462` → `net472`.
- **`Implementation/AWSTracingPipelineHandler.cs`** (L108–114) — the `ImmutableCredentials`
  property is gone; rewrite credential/region capture via `Identity`/`ExplicitAWSCredentials`.
  **This is the one genuinely ADOT-specific handler change** — upstream deleted this block, so
  there is no upstream reference for it.
- **`Implementation/AWSClientsInstrumentation.cs`** (L16–68) — remove the `IsAwsSdkV4()` bail-out
  guard (it currently *skips* instrumentation on v4). Also note the `ImmutableCredentials`-type
  probe (L50–51) is a false signal even today, since the type was not removed.
- **`Implementation/SqsRequestContextHelper.cs` / `SnsRequestContextHelper.cs`** — already
  null-guard `MessageAttributes`; confirm behavior matches upstream (guard-and-return on null).
- `AWSLlmModelProcessor.cs`, `AWSServiceHelper.cs`, `AWSServiceType.cs`, `AWSSemanticConventions.cs`
  — **no SDK-version-sensitive code** (reflection + JSON + string maps); expected to port as-is.

### SigV4 exporters — `src/AWS.Distro.OpenTelemetry.AutoInstrumentation/` (ADOT-specific, no upstream reference)
- **`OtlpAwsSpanExporter/OtlpAwsSpanExporter.cs`**
- **`OtlpAwsSpanExporter/IAwsAuthenticator.cs`** (L43) — `Sign(...)` `BaseIdentity` break.
- **`SigV4OtlpLogExporter.cs`** (L91) — `Sign(...)` `BaseIdentity` break.
- **`AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj`** — bump `AWSSDK.Core` (3.7.400) and
  `AWSSDK.XRay` (3.7.300) to v4; `net462` → `net472`.

### Test apps
- **`test/contract-tests/images/applications/TestSimpleApp.AWSSDK.Core/TestSimpleApp.AWSSDK.Core.csproj`**
  — bump all AWSSDK packages `3.7.500` → v4.

## Test coverage status

Structural finding: **there is no in-repo unit-test project for the vendored package.** The
distro unit test `AwsMetricAttributesGeneratorTest.TestSdkClientSpanWithRemoteResourceAttributes`
synthesizes span tags as *inputs* and only tests the downstream metric generator — it stays green
even if extraction breaks. **The contract tests are the only thing exercising the migrated code.**

### Addressed in PR #433 (on v3, pre-migration)
- Fixed harness bug: `request_specific_attributes` / `response_specific_attributes` were passed
  but never asserted at span level (activated `aws.dynamodb.table.arn`, `aws.s3.bucket`).
- Span-level credential attrs `aws.auth.account.access_key` / `aws.auth.region` (cross-account).
- Common per-call attrs `aws.requestId` / `http.response_content_length` (real LocalStack calls).
- `db.system=dynamodb`.

### Known coverage gaps (deferred, tracked here)
- **`aws.region`** — not asserted anywhere. Sample-app clients set only `ServiceURL` (LocalStack
  host), so `DetermineRegion` returns null and the tag is omitted. Needs a client that resolves a
  concrete region.
- **Lambda** (`aws.lambda.function.name` / `.arn` / `aws.lambda.resource_mapping.id`) — no
  sample-app infrastructure (no `AWSSDK.Lambda` package, client, handler, or routes). Deferred to
  a planned E2E test (the response-side `Configuration.FunctionArn` reflection and `FunctionName`
  ARN-normalization are entirely untested E2E).

## Risks (from the SIM investigation)
- **Assembly version-binding conflict (highest):** ADOT ships `AWSSDK.Core.dll` onto the customer
  process's resolution path (auto-instrumentation `DOTNET_ADDITIONAL_DEPS` / shared store), so a
  customer app on v3 can be force-bound to v4 and throw `MissingMethodException`/`TypeLoadException`.
  Validate the built `.deps.json`/store layout before shipping.
- **EKS/Lambda auto-injection** can roll customers forward to a v4 ADOT with no change on their
  side → binding conflict at deploy time with no easy rollback. Consider staging as a new major
  version with v3 layer/init-container tags kept available and documented for pinning.
- **net462 drop** — customers on .NET Framework 4.6.2 lose the AWS SDK instrumentation path.

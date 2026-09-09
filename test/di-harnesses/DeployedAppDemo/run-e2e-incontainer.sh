#!/bin/bash
# In-container half of the Linux DI E2E. Runs as root in a glibc .NET SDK image.
# Mirrors the GA enable path (instrument.sh) on the UNMODIFIED SampleApp and
# asserts the same 5-check contract as run-demo.sh. exit = number of failed checks.
set -uo pipefail
DEMO=/demo
DARCH="${DARCH:-arm64}"
DISTRO="$DEMO/OpenTelemetryDistribution-linux-$DARCH"
PORT=2000
LOGDIR="$DEMO/logs-linux"
rm -rf "$LOGDIR"; mkdir -p "$LOGDIR"

MOCK_DLL="$DEMO/MockBackend/bin/Release/net8.0/MockBackend.dll"
APP_DLL="$DEMO/SampleApp/bin/Release/net8.0/SampleApp.dll"
MOCK_OUT="$LOGDIR/mock-backend.out"
APP_OUT="$LOGDIR/app.out"

cleanup() { kill "${MOCK_PID:-}" "${APP_PID:-}" 2>/dev/null; }
trap cleanup EXIT INT TERM

# ── start the (empty) mock backend, then create a probe + a breakpoint ──────
echo "[c] starting mock backend…"
dotnet "$MOCK_DLL" $PORT > "$MOCK_OUT" 2>&1 &
MOCK_PID=$!
sleep 2

echo "[c] creating probe (OrderService.Process) + breakpoint (CheckoutService.Complete)…"
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"PROBE","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"OrderService","MethodName":"Process","FilePath":"OrderService.cs"}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":["orderId"],"CaptureReturn":true,"CaptureLimits":{"MaxHits":100}}}}' >/dev/null
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"CheckoutService","MethodName":"Complete","FilePath":"CheckoutService.cs"}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":["cartId"],"CaptureReturn":true,"CaptureLimits":{"MaxHits":3}}}}' >/dev/null
sleep 1

# ── enable DI on the unmodified app (GA env vars, mirroring instrument.sh) ──
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so"
export OTEL_DOTNET_AUTO_HOME="$DISTRO"
export DOTNET_STARTUP_HOOKS="$DISTRO/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$LOGDIR"
export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED=true
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL="http://127.0.0.1:$PORT"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL=5
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL=5
export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://127.0.0.1:$PORT/v1/logs"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_SERVICE_NAME=sample-app
export OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=staging

# ── run the app; wait for the weave, then the snapshot ──────────────────────
echo "[c] launching SampleApp under the profiler…"
dotnet "$APP_DLL" > "$APP_OUT" 2>&1 &
APP_PID=$!

echo "[c] waiting for native ReJIT weave (up to 45s)…"
# Key off the method-level ReJIT finish line (hash-independent) so this works whether the LocationHash is
# the local mock's synthetic id or a real beta-assigned hex hash.
WOVE=0
for i in $(seq 1 45); do
  NATIVE_LOG=$(ls -t "$LOGDIR"/*dotnet-Native.log 2>/dev/null | head -1)
  if [ -n "$NATIVE_LOG" ] && grep -qE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.(OrderService.Process|CheckoutService.Complete)" "$NATIVE_LOG" 2>/dev/null; then
    WOVE=1; break
  fi
  sleep 1
done
echo "[c] weave signal=$WOVE; waiting for OTLP snapshot (up to 20s)…"
SNAP=0
for i in $(seq 1 20); do
  if grep -q "\[SNAPSHOT-RECEIVED\]" "$MOCK_OUT" 2>/dev/null; then SNAP=1; break; fi
  sleep 1
done
sleep 1
kill "$APP_PID" 2>/dev/null; wait "$APP_PID" 2>/dev/null

# ── VERIFY: 5-check contract ────────────────────────────────────────────────
echo "############################################################################"
echo "#  LINUX E2E VERIFICATION (exit code = number of failed checks)"
echo "############################################################################"
NATIVE_LOG=$(ls -t "$LOGDIR"/*dotnet-Native.log 2>/dev/null | head -1)
FAILS=0
check() { if [ "$2" = "1" ]; then echo "  [PASS] $1"; else echo "  [FAIL] $1"; FAILS=$((FAILS+1)); fi; }

WOVE_FINISHED=0
[ -n "$NATIVE_LOG" ] && grep -qE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.OrderService.Process" "$NATIVE_LOG" 2>/dev/null && WOVE_FINISHED=1
check "native profiler ReJIT-wove OrderService.Process" "$WOVE_FINISHED"

check "snapshot LogRecord received over OTLP /v1/logs" "$SNAP"

SNAP_ATTRS=0
grep -qE "\[SNAPSHOT-RECEIVED\] event=aws.dynamic_instrumentation.snapshot .*method=Process level=method" "$MOCK_OUT" 2>/dev/null && SNAP_ATTRS=1
check "snapshot has event.name + aws.di.method_name=Process + level=method" "$SNAP_ATTRS"

SNAP_BODY=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "order-" \
  && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "processed:order-" \
  && SNAP_BODY=1
check "snapshot body captured the orderId argument + return value" "$SNAP_BODY"

STATUS_OK=0
grep -q "report-instrumentation-configuration-status" "$MOCK_OUT" 2>/dev/null && STATUS_OK=1
check "agent reported instrumentation status to the backend" "$STATUS_OK"

# (beta mode only) the config API leg actually round-tripped through REAL beta with no forward errors.
if [ -n "${DI_BETA_ENDPOINT:-}" ]; then
  # Round-trip is proven when the SIGNED requests reach beta and beta evaluates them. create is 2xx on a
  # fresh config OR 409 (Conflict) when it already exists from a prior run — both prove signed connectivity
  # (409 means beta authenticated, parsed, and matched an existing LocationHash). Configs persist until
  # explicit cleanup, so 409 is the expected steady-state, not a failure. list must be 2xx.
  BETA_OK=0
  if grep -qE "\[BETA-FORWARD\] list-instrumentation-configurations → HTTP 2" "$MOCK_OUT" 2>/dev/null \
     && grep -qE "\[BETA-FORWARD\] create-instrumentation-configuration → HTTP (2..|409)" "$MOCK_OUT" 2>/dev/null; then
    BETA_OK=1
  fi
  check "config API leg (list+create) round-tripped through REAL beta ($DI_BETA_ENDPOINT)" "$BETA_OK"
fi

echo "════════════════════════════════════════════════════════════════════════════"
echo "[c] app stdout (first 6 lines):"; head -6 "$APP_OUT" 2>/dev/null | sed 's/^/    /'
if [ -n "$NATIVE_LOG" ]; then
  echo "[c] native-profiler proof:"
  grep -E "received id: probe-orderservice|CallTarget_RewriterCallback.*OrderService.Process" "$NATIVE_LOG" 2>/dev/null \
    | sed -E 's/^\[[^]]*\] \[[^]]*\] \[[^]]*\] /    /' | head -4
fi
grep -E "\[SNAPSHOT-RECEIVED\]|\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | head -4 | sed 's/^/    /'
echo "════════════════════════════════════════════════════════════════════════════"
if [ "$FAILS" -eq 0 ]; then echo "  ✅ FULL LINUX E2E PASS"; else echo "  ❌ E2E FAILED: $FAILS check(s). mock=$MOCK_OUT app=$APP_OUT"; fi
exit "$FAILS"

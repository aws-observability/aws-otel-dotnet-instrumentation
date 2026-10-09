#!/bin/bash
# ============================================================================
#  BULK APPLY measurement — in-container half.
#
#  Answers: is line/method-level probe apply cost LINEAR in the number of probes
#  (N x ~131us) or AMORTISED by the profiler's batched ReJIT request?
#
#  Why this needs the real app and not a microbenchmark: RequestReJIT only
#  recompiles methods that ALREADY have JIT-compiled code. The earlier
#  microbenchmark pointed probes at methods that had never been called, so the
#  profiler skipped 19 of 20 ("Request ReJIT done for 1 methods") and the
#  falling per-probe curve was an artifact of the denominator. Here the app runs
#  DI_BULK_MODE=1, which calls all N BulkService methods 500x BEFORE any config
#  exists, so every target is genuinely JIT'd and every probe causes real work.
#
#  GROUND TRUTH GATE: the profiler's own native log line
#      "Request ReJIT done for N methods"
#  must report N (not 1). If it reports 1, the measurement is void — same as
#  [P0c] gating the microbenchmark's suspension proxy.
# ============================================================================
set -uo pipefail
DEMO=/demo
DARCH="${DARCH:-arm64}"
DISTRO="$DEMO/OpenTelemetryDistribution-linux-$DARCH"
PORT=2000
LOGDIR="$DEMO/logs-bulk"
rm -rf "$LOGDIR"; mkdir -p "$LOGDIR"

N="${DI_BULK_METHODS:-20}"

MOCK_DLL="$DEMO/MockBackend/bin/Release/net8.0/MockBackend.dll"
APP_DLL="$DEMO/SampleApp/bin/Release/net8.0/SampleApp.dll"
MOCK_OUT="$LOGDIR/mock-backend.out"
APP_OUT="$LOGDIR/app.out"

cleanup() { kill "${MOCK_PID:-}" "${APP_PID:-}" 2>/dev/null; }
trap cleanup EXIT INT TERM

echo "[c] starting mock backend…"
dotnet "$MOCK_DLL" $PORT > "$MOCK_OUT" 2>&1 &
MOCK_PID=$!
sleep 2

# ── enable DI (GA env vars). Long poll interval so NO config is fetched until we
#    have created all N and the app has finished pre-warming: that makes the
#    apply a single batched OnConfigurationsChanged pass over N configs.
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so"
export OTEL_DOTNET_AUTO_HOME="$DISTRO"
export DOTNET_STARTUP_HOOKS="$DISTRO/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$LOGDIR"
export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED=true
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL="http://127.0.0.1:$PORT"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL=10
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL=600
export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://127.0.0.1:$PORT/v1/logs"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_SERVICE_NAME=sample-app
export OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=staging
export DI_BULK_MODE=1
export DI_BULK_METHODS="$N"

echo "[c] launching SampleApp (DI_BULK_MODE=1, $N methods) under the profiler…"
dotnet "$APP_DLL" > "$APP_OUT" 2>&1 &
APP_PID=$!

# Wait for the app to finish pre-warming so every target method is JIT-compiled
# BEFORE any probe config exists. This is the whole point.
echo "[c] waiting for pre-warm to complete (all $N methods JIT'd)…"
WARM=0
for i in $(seq 1 60); do
  if grep -q "BULK-WARM-COMPLETE" "$APP_OUT" 2>/dev/null; then WARM=1; break; fi
  sleep 1
done
if [ "$WARM" != "1" ]; then
  echo "[c] FATAL: app never signalled BULK-WARM-COMPLETE — targets may not be JIT'd."
  sed -n '1,25p' "$APP_OUT"
  exit 99
fi
echo "[c] pre-warm confirmed."

# ── create N probe configs, one per BulkService.HandlerK ────────────────────
echo "[c] creating $N probe configs (BulkService.Handler0..$((N-1)))…"
for k in $(seq 0 $((N-1))); do
  curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
    -H "Content-Type: application/json" -d "{
    \"AccountId\":\"000000000000\",\"Service\":\"sample-app\",\"Environment\":\"staging\",
    \"InstrumentationType\":\"PROBE\",\"SignalType\":\"SNAPSHOT\",
    \"Location\":{\"CodeLocation\":{\"Language\":\"Dotnet\",\"CodeUnit\":\"MyCompany.Orders\",\"ClassName\":\"BulkService\",\"MethodName\":\"Handler$k\",\"FilePath\":\"BulkService.cs\"}},
    \"CaptureConfiguration\":{\"CodeCapture\":{\"CaptureArguments\":[\"x\"],\"CaptureReturn\":true,\"CaptureLimits\":{\"MaxHits\":5}}}}" >/dev/null
done
echo "[c] $N configs created; next poll (<=10s) applies them in ONE batch."

# ── wait for the weave to land on all N ─────────────────────────────────────
echo "[c] waiting for ReJIT weave of all $N methods (up to 90s)…"
WOVE_N=0
for i in $(seq 1 90); do
  NATIVE_LOG=$(ls -t "$LOGDIR"/*Native.log 2>/dev/null | head -1)
  if [ -n "$NATIVE_LOG" ]; then
    # grep -c can emit a trailing newline / multiple lines when several logs match; collapse to one int.
    WOVE_N=$(grep -hcE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.BulkService.Handler" "$NATIVE_LOG" 2>/dev/null | head -1)
    WOVE_N="${WOVE_N:-0}"
    [ "$WOVE_N" -ge "$N" ] 2>/dev/null && break
  fi
  sleep 1
done

sleep 3
NATIVE_LOG=$(ls -t "$LOGDIR"/*Native.log 2>/dev/null | head -1)

echo "############################################################################"
echo "#  BULK APPLY RESULTS  (N=$N)"
echo "############################################################################"

echo "[1] GROUND-TRUTH GATE — profiler's own ReJIT request counts:"
grep -oE "Request ReJIT done for [0-9]+ methods" "$NATIVE_LOG" 2>/dev/null | sort | uniq -c | sed 's/^/    /'
MAXREJIT=$(grep -oE "Request ReJIT done for [0-9]+ methods" "$NATIVE_LOG" 2>/dev/null \
  | grep -oE '[0-9]+' | sort -n | tail -1)
MAXREJIT="${MAXREJIT:-0}"
echo "    largest single batch = $MAXREJIT methods (need >1 for a valid bulk measurement)"

echo "[2] AddInstrumentations batch sizes handed to the profiler:"
grep -oE "AddInstrumentations: received id: [^ ]+ from managed side with [0-9]+ integrations" "$NATIVE_LOG" 2>/dev/null \
  | sed 's/^/    /' | tail -25

echo "[3] Methods actually ReJIT-woven: $WOVE_N / $N"
grep -oE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.BulkService.Handler[0-9]+" "$NATIVE_LOG" 2>/dev/null \
  | sort -u | wc -l | sed 's/^/    distinct handlers woven: /'

echo "[4] Profiler callback timing (Stats line — CallTargetRequestRejit is the apply cost):"
grep -oE "Stats: Total time: .*" "$NATIVE_LOG" 2>/dev/null | tail -1 | sed 's/^/    /'

echo "[5] ReJIT timing from the native log (first->last weave of the batch):"
grep -E "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.BulkService.Handler" "$NATIVE_LOG" 2>/dev/null \
  | head -1 | sed 's/^/    first: /'
grep -E "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.BulkService.Handler" "$NATIVE_LOG" 2>/dev/null \
  | tail -1 | sed 's/^/    last:  /'

echo "[6] Snapshots received (proves the woven probes actually captured):"
grep -c "\[SNAPSHOT-RECEIVED\]" "$MOCK_OUT" 2>/dev/null | sed 's/^/    /'
grep -oE "\[SNAPSHOT-RECEIVED\].*method=Handler[0-9]+" "$MOCK_OUT" 2>/dev/null \
  | grep -oE "Handler[0-9]+" | sort -u | wc -l | sed 's/^/    distinct handlers that captured: /'

kill "$APP_PID" 2>/dev/null; wait "$APP_PID" 2>/dev/null

echo "════════════════════════════════════════════════════════════════════════════"
echo "  VERDICT"
if [ "$WOVE_N" -lt "$N" ] 2>/dev/null; then
  echo "  ❌ SETUP FAILED — only $WOVE_N/$N methods were woven. Targets likely not JIT'd."
  exit 1
fi

echo "  ✅ All $N methods genuinely ReJIT-woven (pre-warm worked; no skipped targets)."
if [ "$MAXREJIT" -gt 1 ] 2>/dev/null; then
  echo "  ✅ Profiler batched up to $MAXREJIT methods per ReJIT request — apply amortises."
else
  echo "  ⚠️  FINDING: every ReJIT request carried exactly 1 method, and the agent made"
  echo "      $N separate AddInstrumentations calls (see [2]). Bulk apply is therefore"
  echo "      NOT batched today: DynamicInstrumentationManager.OnConfigurationsChangedLocked"
  echo "      loops per-config calling ProfilerTranslator.ApplyInstrumentation one at a time."
  echo "      => apply cost is LINEAR in probe count: N x single-probe cost."
  echo "      The profiler's native AddLineProbes/AddInstrumentations CAN take an array, so"
  echo "      the batching opportunity exists but is unused by the managed caller."
fi
exit 0

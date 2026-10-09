#!/bin/bash
# ============================================================================
#  LIVE SERVICE — a running app with DI enabled, and nothing else.
#
#  No narration, no assertions, no scripted sequence. This starts the backend
#  and an ordinary application through the real ADOT distribution, then just
#  keeps serving traffic until you stop it.
#
#  It exists because run-demo.sh is a PROOF (it stops the app to freeze the log
#  and assert on it). Showing that a customer can attach and detach probes
#  whenever they like needs the opposite: a service that stays up indefinitely
#  while a human takes their time.
#
#    Terminal 1:  bash run-live.sh
#    Terminal 2:  bash di-console.sh
#
#  Stop with Ctrl-C.
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
[ -d "$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation" ] || {
  echo "ERROR: '$REPO_ROOT' is not the aws-otel-dotnet-instrumentation repo."
  echo "  Set DI_REPO_ROOT=/path/to/aws-otel-dotnet-instrumentation"
  exit 1
}
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"
PORT="${DI_PORT:-2000}"
LOGDIR="$SCRIPT_DIR/logs"

# Y and RD are used by the credential warning and the local-mock label. Declared here because `set -u` turns
# a missing colour variable into a hard abort — which is exactly what happened: "Y: unbound variable" killed
# the run before it printed anything.
if [ -t 1 ]; then
  B=$'\033[1m'; D=$'\033[2m'; G=$'\033[32m'; C=$'\033[36m'; Y=$'\033[33m'; RD=$'\033[31m'; R=$'\033[0m'
else
  B=''; D=''; G=''; C=''; Y=''; RD=''; R=''
fi

rm -rf "$LOGDIR"; mkdir -p "$LOGDIR"
MOCK_OUT="$LOGDIR/mock-backend.out"
APP_OUT="$LOGDIR/app.out"

OS=$(uname -s | tr '[:upper:]' '[:lower:]'); ARCH=$(uname -m)
case "$ARCH" in x86_64) ARCH="x64";; aarch64|arm64) ARCH="arm64";; esac
case "$OS" in
  linux)  PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so";;
  darwin) PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib";;
esac
[ -f "$PROFILER_PATH" ] || { echo "ERROR: native profiler not found: $PROFILER_PATH"; exit 1; }

# Same content check run-demo.sh does. Without the forked profiler, line-level probes report a clean error
# instead of capturing — and a console session would look broken for a deployment reason.
PROFILER_SYMS="$(nm -gU "$PROFILER_PATH" 2>/dev/null || true)"
if printf '%s' "$PROFILER_SYMS" | grep -q "_AddLineProbes"; then
  LINE_OK="${G}yes${R}"
else
  LINE_OK="${D}no — stock upstream profiler, line-level will report an error${R}"
fi

MOCK_DLL="$SCRIPT_DIR/MockBackend/bin/Release/net8.0/MockBackend.dll"
APP_DLL="$SCRIPT_DIR/SampleApp/bin/Release/net8.0/SampleApp.dll"

cleanup() {
  echo
  echo "  stopping…"
  kill "${MOCK_PID:-}" "${APP_PID:-}" "${COLLECTOR_PID:-}" 2>/dev/null
  pkill -f "otelcol-contrib" 2>/dev/null
  pkill -f "$MOCK_DLL" 2>/dev/null
  pkill -f "$APP_DLL" 2>/dev/null
}
trap cleanup EXIT INT TERM

if lsof -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "port $PORT busy — clearing a leftover process…"
  lsof -tiTCP:"$PORT" -sTCP:LISTEN 2>/dev/null | xargs kill 2>/dev/null
  sleep 1
fi

echo "Building…"
DI_PROJ="$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/AWS.Distro.OpenTelemetry.DynamicInstrumentation.csproj"
( cd "$REPO_ROOT" && dotnet build "$DI_PROJ" -c Release -f net8.0 -v q --nologo ) >/dev/null || { echo "DI build failed"; exit 1; }
cp "$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/bin/Release/net8.0/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" \
   "$DISTRO_DIR/net/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" || { echo "DI dll refresh failed"; exit 1; }
( cd "$SCRIPT_DIR/MockBackend" && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "mock build failed"; exit 1; }
( cd "$SCRIPT_DIR/SampleApp"   && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "app build failed"; exit 1; }

# ── Beta mode (opt-in) ──────────────────────────────────────────────────────
# With DI_BETA_ENDPOINT set, the local backend stops being a mock for the CONFIG leg: it SigV4-signs and
# forwards list/create/delete/report to the real Application Signals beta endpoint, playing the role the
# CloudWatch Agent plays in production. Everything the operator does through di-console.sh then hits real AWS.
#
# Snapshots do NOT go through this API in production either — they go to the CloudWatch Agent's OTLP logs
# receiver, which forwards them to CloudWatch Logs. So beta mode covers the config leg only; the snapshot leg
# is handled separately by DI_SNAPSHOTS_TO_CW=1 (see the snapshot-destination block below).
# REAL BETA IS THE DEFAULT. The config API is live for .NET, so a run that quietly used a mock would be
# demonstrating less than the product can actually do. Opt out explicitly with DI_LOCAL_MOCK=1 (offline, no
# credentials) — the banner always states which one is in use, so it can never be ambiguous.
if [ "${DI_LOCAL_MOCK:-0}" != "1" ] && [ -z "${DI_BETA_ENDPOINT:-}" ]; then
  DI_BETA_ENDPOINT="$DI_BETA_ENDPOINT_URL"
fi

if [ -n "${DI_BETA_ENDPOINT:-}" ]; then
  export DI_BETA_ENDPOINT
  export DI_BETA_REGION="${DI_BETA_REGION:-us-west-2}"
  export DI_BETA_SERVICE="${DI_BETA_SERVICE:-application-signals}"
  # Says nothing about snapshots — that is the `snapshots` banner line's job, and stating it here as "local"
  # contradicted the truth once DI_SNAPSHOTS_TO_CW started sending them to real CloudWatch Logs.
  BACKEND_LABEL="${G}REAL BETA${R} for the config API (${DI_BETA_ENDPOINT})"
  # Expired credentials 403 every call and look exactly like a total product regression. Say which chain is
  # in use up front, so the first thing to check is visible before anything appears broken.
  if [ -n "${AWS_ACCESS_KEY_ID:-}" ]; then
    BACKEND_LABEL="$BACKEND_LABEL ${D}[creds: AWS_* env vars]${R}"
  else
    BACKEND_LABEL="$BACKEND_LABEL ${D}[creds: default chain]${R}"
  fi
  # FAIL BEFORE THE DEMO STARTS, not thirty seconds in. With no credentials every call 403s and the symptom is
  # indistinguishable from the feature being broken — which is the single most expensive way for this to go
  # wrong in front of an audience.
  if [ -z "${AWS_ACCESS_KEY_ID:-}" ] && [ ! -f "$HOME/.aws/credentials" ]; then
    echo "${Y}WARNING${R}: beta mode with no AWS_* env vars and no ~/.aws/credentials."
    echo "  Every config call will return 403 and will look exactly like a product failure."
    echo "  Refresh credentials, or run offline with: DI_LOCAL_MOCK=1"
    echo
  fi
else
  BACKEND_LABEL="${Y}LOCAL MOCK${R} (DI_LOCAL_MOCK=1) — config API is simulated, not real"
fi

dotnet "$MOCK_DLL" > "$MOCK_OUT" 2>&1 &
MOCK_PID=$!
sleep 2

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER='{918728DD-259F-4A6A-AC2B-B85E1B658318}'
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
# WITHOUT THIS THE PLUGIN NEVER LOADS. OTEL_DOTNET_AUTO_HOME is how the auto-instrumentation locates the
# distribution, and the plugin named in OTEL_DOTNET_AUTO_PLUGINS is what HOSTS Dynamic Instrumentation. Omit
# it and the app runs happily with the profiler attached, writes profiler logs, and DI simply never starts —
# no error, no poll, no capture. Measured: the agent made ZERO calls to the configuration API.
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$LOGDIR"
export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED=true
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL="http://127.0.0.1:$PORT"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL=5
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL=5
# SNAPSHOT DESTINATION.
#   default            -> the local backend, which decodes and prints each snapshot (fast, offline, verifiable)
#   DI_SNAPSHOTS_TO_CW -> a real OTel Collector on :4316 that ships them to REAL CloudWatch Logs
#
# The collector stands in for the CloudWatch Agent's snapshot leg. It has to, because the PUBLIC CloudWatch
# Agent cannot ingest OTLP logs at all — its repo has OTLP receivers for metrics and traces only, verified by
# walking the tree — and the DI-capable agent build is not publicly downloadable. The collector uses the same
# exporter the agent would (awscloudwatchlogs), so what lands in CloudWatch Logs is the real snapshot body.
if [ "${DI_SNAPSHOTS_TO_CW:-0}" = "1" ]; then
  COLLECTOR_BIN="$SCRIPT_DIR/collector/otelcol-contrib"
  COLLECTOR_CFG="$SCRIPT_DIR/collector/config.yaml"
  [ -x "$COLLECTOR_BIN" ] || { echo "ERROR: collector not found at $COLLECTOR_BIN"; exit 1; }
  export AWS_REGION="${AWS_REGION:-${DI_BETA_REGION:-us-west-2}}"
  # Overridable so the demo can write to whatever group the console is actually being watched in.
  export DI_LOG_GROUP="${DI_LOG_GROUP:-/aws/application-signals/dynamic-instrumentation}"
  export DI_LOG_STREAM="${DI_LOG_STREAM:-sample-app-staging}"
  export DI_CW_ROLE_ARN="${DI_CW_ROLE_ARN:-}"

  # WHICH ACCOUNT AM I WRITING TO? Resolve it BEFORE starting, and say so.
  #
  # This is the check that was missing. The collector inherits whatever credentials are in the environment —
  # which here are the BETA account's — so snapshots landed in the beta account while the console was open on a
  # different one. Nothing failed; 70+ snapshots were written correctly to an account nobody was looking at.
  # An export that succeeds in the wrong place is worse than one that fails, because there is no error to find.
  #
  # DI_CW_PROFILE picks a different identity for the collector only (the beta proxy keeps using the ambient
  # credentials for the config leg). DI_CW_ACCOUNT, if set, is enforced — a mismatch aborts rather than writing
  # somewhere unintended.
  if [ -n "${DI_CW_PROFILE:-}" ]; then
    export AWS_PROFILE="$DI_CW_PROFILE"
    CW_IDENT_HINT="profile $DI_CW_PROFILE"
  else
    CW_IDENT_HINT="ambient credentials"
  fi

  CW_ACCOUNT="$(aws sts get-caller-identity --query Account --output text 2>/dev/null || echo unknown)"
  if [ -n "${DI_CW_ACCOUNT:-}" ] && [ "$CW_ACCOUNT" != "$DI_CW_ACCOUNT" ]; then
    echo "${RD}ERROR${R}: collector would write to account ${B}$CW_ACCOUNT${R}, but DI_CW_ACCOUNT=${B}$DI_CW_ACCOUNT${R}."
    echo "  Refusing to start — snapshots would land in an account you are not watching."
    echo "  Set DI_CW_PROFILE to an identity in $DI_CW_ACCOUNT, or unset DI_CW_ACCOUNT to accept $CW_ACCOUNT."
    exit 1
  fi
  if [ -z "${AWS_ACCESS_KEY_ID:-}" ] && [ ! -f "$HOME/.aws/credentials" ]; then
    echo "${RD}ERROR${R}: DI_SNAPSHOTS_TO_CW=1 needs AWS credentials — the collector writes to CloudWatch Logs."
    exit 1
  fi
  "$COLLECTOR_BIN" --config "$COLLECTOR_CFG" > "$LOGDIR/collector.out" 2>&1 &
  COLLECTOR_PID=$!
  sleep 4
  if ! lsof -iTCP:4316 -sTCP:LISTEN >/dev/null 2>&1; then
    echo "${RD}ERROR${R}: collector failed to start — see $LOGDIR/collector.out"
    exit 1
  fi
  export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://127.0.0.1:4316/v1/logs"
  SNAPSHOT_LABEL="${G}REAL CloudWatch Logs${R} via OTel Collector :4316"
  SNAPSHOT_DETAIL_1="  ${D}   group   $DI_LOG_GROUP${R}"
  SNAPSHOT_DETAIL_2="  ${D}   region  $AWS_REGION   account ${B}$CW_ACCOUNT${R}${D} ($CW_IDENT_HINT)${R}"
else
  export OTEL_AWS_OTLP_LOGS_ENDPOINT="http://127.0.0.1:$PORT/v1/logs"
  SNAPSHOT_LABEL="local sink, decoded and printed ${D}(set DI_SNAPSHOTS_TO_CW=1 to send to real CloudWatch Logs)${R}"
  SNAPSHOT_DETAIL_1=""; SNAPSHOT_DETAIL_2=""
fi
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_SERVICE_NAME=sample-app
export OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=staging
export RESOURCE_DETECTORS_ENABLED="${RESOURCE_DETECTORS_ENABLED:-false}"

# METRICS OFF. Nothing here consumes them, and the exporter retries a collector that is not running, so the
# agent log fills with "Connection refused (localhost:4318)" every 60s. Harmless to DI, but during a demo an
# [Error] in the log reads as the feature failing. Measured on the last run: two such errors in two minutes.
#
# Only METRICS, deliberately. Traces are left alone because the plugin that hosts DI initialises alongside the
# tracer provider (Plugin.cs OnTracerProviderInitialized), and turning the traces exporter off is exactly what
# stops DI from starting in the Node.js agent. Not worth risking here for log tidiness.
export OTEL_METRICS_EXPORTER="${OTEL_METRICS_EXPORTER:-none}"

# ~14 hours at one tick per second. The point is that the presenter never has to think about it.
export DI_TICKS="${DI_TICKS:-50000}"

dotnet "$APP_DLL" > "$APP_OUT" 2>&1 &
APP_PID=$!

echo
echo "${C}${B}══════════════════════════════════════════════════════════════════════════${R}"
echo "${C}${B}  LIVE SERVICE RUNNING${R}"
echo "${C}${B}══════════════════════════════════════════════════════════════════════════${R}"
echo "  service          sample-app / staging"
echo "  backend          $BACKEND_LABEL"
echo "  snapshots        $SNAPSHOT_LABEL"
[ -n "${SNAPSHOT_DETAIL_1:-}" ] && echo "$SNAPSHOT_DETAIL_1"
[ -n "${SNAPSHOT_DETAIL_2:-}" ] && echo "$SNAPSHOT_DETAIL_2"
echo "  DI enabled       yes, by environment variables only (no code change)"
echo "  configurations   ${B}none${R} — it is running completely uninstrumented"
echo "  line-level       $LINE_OK"
echo "  poll interval    5s"
echo "  app log          $APP_OUT"
echo "  backend log      $MOCK_OUT"
echo
echo "  ${B}Now open a second terminal and drive it:${R}"
echo "      cd $SCRIPT_DIR"
echo "      bash di-console.sh"
echo
echo "  ${D}Ctrl-C here when you are done.${R}"
echo "${C}${B}══════════════════════════════════════════════════════════════════════════${R}"
echo
echo "  ${D}tailing the app (one tick per second)…${R}"
echo

# Surface the app's own heartbeat so the audience can see it is still serving while probes come and go — and
# so a crash is visible immediately rather than looking like "DI stopped capturing".
tail -f "$APP_OUT" &
TAIL_PID=$!

# Wait on the APP, not on tail: if the app dies, this returns and the trap cleans up, instead of sitting on a
# tail of a dead process and implying the service is still healthy.
wait "$APP_PID"
echo
echo "  ${D}the application exited (DI_TICKS reached, or it crashed — see $APP_OUT)${R}"

#!/bin/bash
# ============================================================================
#  .NET Dynamic Instrumentation — FULL PIPELINE DEMO (one command)
#
#  Runs every real component end to end:
#    1. CREATE a config on the LIVE backend  (SigV4)
#    2. Show the LIVE backend REJECT Language="Dotnet"  (the one GA blocker)
#    3. FETCH the config back LIVE            → real response bytes
#    4. PARSE those bytes via the real .NET client + model
#    5. APPLY via ProfilerTranslator → REAL native CLR profiler ReJIT weave
#    6. INVOKE the target → capture args/return through the profiler
#    7. REPORT status back to the LIVE backend → UnprocessedStatusEvents:[]
#    8. DELETE (cleanup)
#
#  Requires: AWS creds exported (temp/Isengard), the OpenTelemetryDistribution
#  present. Backend calls are SigV4-signed Python; the profiler stage is a real
#  .NET process with the native profiler loaded.
# ============================================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"
STATE="/tmp/pipeline-demo"
mkdir -p "$STATE"

hr(){ echo "════════════════════════════════════════════════════════════════════════════"; }
pause(){ if [ "${DEMO_PAUSE:-0}" = "1" ]; then read -rp "   ⏎ continue…" _; fi; }

if [ -z "${AWS_ACCESS_KEY_ID:-}" ]; then echo "ERROR: export AWS creds first."; exit 1; fi

# Resolve native profiler for this OS/arch
OS=$(uname -s | tr '[:upper:]' '[:lower:]'); ARCH=$(uname -m)
case "$ARCH" in x86_64) ARCH="x64";; aarch64|arm64) ARCH="arm64";; esac
case "$OS" in
  linux)  PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so";;
  darwin) PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib";;
esac

hr; echo "  .NET DYNAMIC INSTRUMENTATION — FULL PIPELINE (live backend + native profiler)"; hr

echo; echo "▶ STAGE 1 — CREATE a config on the LIVE backend (SigV4)"
python3 "$SCRIPT_DIR/backend.py" create || exit 1; pause

echo; echo "▶ STAGE 2 — LIVE backend REJECTS Language=\"Dotnet\" (the single GA blocker)"
python3 "$SCRIPT_DIR/backend.py" dotnet-reject; pause

echo; echo "▶ STAGE 3 — FETCH configs back from the LIVE backend"
python3 "$SCRIPT_DIR/backend.py" list || exit 1; pause

echo; echo "▶ STAGES 4–6 — PARSE (real client) → WEAVE (native profiler) → CAPTURE"
echo "   launching a real .NET process with the native profiler loaded…"; echo
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
export DEMO_LOCATION_HASH="$(cat "$STATE/location-hash.txt" 2>/dev/null)"
( cd "$SCRIPT_DIR" && dotnet run -c Release --framework net8.0 -- "$STATE/list-response.json" "$STATE/status-body.json" )
PROFILER_RC=$?
pause

if [ $PROFILER_RC -eq 0 ]; then
  echo; echo "▶ STAGE 7 — REPORT status back to the LIVE backend"
  python3 "$SCRIPT_DIR/backend.py" report
fi

echo; echo "▶ STAGE 8 — CLEANUP (delete the demo config)"
python3 "$SCRIPT_DIR/backend.py" delete

echo; hr
if [ $PROFILER_RC -eq 0 ]; then
  echo "  ✅ PIPELINE COMPLETE — live backend ⇄ real .NET client ⇄ native profiler capture"
  echo "     Only GA blocker shown at Stage 2: add \"Dotnet\" to the ProgrammingLanguage enum."
else
  echo "  ❌ profiler stage failed (rc=$PROFILER_RC)"
fi
hr
exit $PROFILER_RC

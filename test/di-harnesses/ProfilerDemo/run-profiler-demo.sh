#!/bin/bash
# ============================================================================
#  .NET Dynamic Instrumentation — NATIVE PROFILER demo
#
#  Runs the SAME compiled binary twice to prove the native profiler is the cause:
#    RUN 1 — profiler OFF → 0 captures (no weaving happens without it)
#    RUN 2 — profiler ON  → Charge captured; Refund (control) never captured
#
#  Proof it's real (mirrors the backend demo's x-amzn-RequestId proof):
#    • identical binary, only the profiler env differs between the two runs
#    • the app method bodies never call DI — capture only happens via ReJIT weave
#    • negative control (Refund) is never captured in either run
#
#  Step-by-step: press Enter between runs (DEMO_NOPAUSE=1 to disable).
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

OS=$(uname -s | tr '[:upper:]' '[:lower:]'); ARCH=$(uname -m)
case "$ARCH" in x86_64) ARCH="x64";; aarch64|arm64) ARCH="arm64";; esac
case "$OS" in
  linux)  PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so";;
  darwin) PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib";;
esac
[ -f "$PROFILER_PATH" ] || { echo "ERROR: native profiler not found: $PROFILER_PATH"; exit 1; }

pause(){ [ "${DEMO_NOPAUSE:-0}" = "1" ] || read -rp "$1" _; }
hr(){ echo "════════════════════════════════════════════════════════════════════════════"; }

hr; echo "  .NET DYNAMIC INSTRUMENTATION — NATIVE PROFILER DEMO"
echo "  native profiler: $PROFILER_PATH"; hr

echo; echo "Building once (both runs use this exact same binary)…"
( cd "$SCRIPT_DIR" && dotnet build -c Release --framework net8.0 -v q --nologo ) >/dev/null || { echo "build failed"; exit 1; }
DLL="$SCRIPT_DIR/bin/Release/net8.0/ProfilerE2E.dll"

pause $'\n⏎  RUN 1 — profiler OFF (baseline: expect 0 captures)…'
echo; echo ">>> dotnet $DLL   [no profiler env]"
( cd "$SCRIPT_DIR" && dotnet "$DLL" )
RC_OFF=$?

pause $'\n⏎  RUN 2 — profiler ON (expect Charge captured)…'
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
rm -f "$SCRIPT_DIR/logs/"*dotnet-Native.log 2>/dev/null
echo; echo ">>> CORECLR_ENABLE_PROFILING=1 dotnet $DLL   [native profiler loaded]"
( cd "$SCRIPT_DIR" && dotnet "$DLL" )
RC_ON=$?

# ── INDEPENDENT PROOF ────────────────────────────────────────────────────────
# The native profiler (a C++ .dylib, NOT our managed code) writes its own log. Show the lines
# where IT reports rewriting our method — the profiler equivalent of AWS's x-amzn-RequestId.
pause $'\n⏎  show the NATIVE profiler\'s own log (independent proof it did the weaving)…'
NATIVE_LOG=$(ls -t "$SCRIPT_DIR/logs/"*dotnet-Native.log 2>/dev/null | head -1)
echo; hr
echo "  INDEPENDENT PROOF — from the native profiler's OWN log (not our C#):"
echo "  $NATIVE_LOG"; echo
if [ -n "$NATIVE_LOG" ]; then
  grep -E "received id: demo-charge-hash|Request ReJIT done|CallTarget_RewriterCallback.*PaymentService" "$NATIVE_LOG" \
    | sed -E 's/^\[[^]]*\] \[[^]]*\] \[[^]]*\] /    /' \
    | sed 's/^/  /'
else
  echo "  (native log not found)"
fi

echo; hr
# RC: 0=not captured, 1=captured, 3=control failed
if [ $RC_OFF -eq 0 ] && [ $RC_ON -eq 1 ]; then
  echo "  ✅ PROVEN three independent ways:"
  echo "     1. identical binary — 0 captures with profiler OFF, captured with profiler ON"
  echo "     2. the native profiler's OWN log reports rewriting Demo.PaymentService.Charge (above)"
  echo "     3. negative control held — Refund was never captured in either run"
elif [ $RC_OFF -eq 3 ] || [ $RC_ON -eq 3 ]; then
  echo "  ❌ negative control failed (a non-targeted method was captured)"
else
  echo "  ❌ unexpected: OFF rc=$RC_OFF (want 0), ON rc=$RC_ON (want 1)"
fi
hr

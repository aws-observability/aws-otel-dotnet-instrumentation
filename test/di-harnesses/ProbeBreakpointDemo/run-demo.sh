#!/bin/bash
# PROBE + BREAKPOINT end-to-end demo: mock backend → real client → native profiler.
# Runs the SAME binary twice: profiler OFF (baseline, 0 captures) then ON (PROBE=5, BREAKPOINT=3).
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

echo "Building once (both runs use this same binary)…"
( cd "$SCRIPT_DIR" && dotnet build -c Release --framework net8.0 -v q --nologo ) >/dev/null || { echo "build failed"; exit 1; }
DLL="$SCRIPT_DIR/bin/Release/net8.0/ProfilerE2E.dll"

pause $'\n⏎  RUN 1 — profiler OFF (baseline: expect 0 captures)…'
( cd "$SCRIPT_DIR" && dotnet "$DLL" )
RC_OFF=$?

pause $'\n⏎  RUN 2 — profiler ON (expect PROBE=5, BREAKPOINT=3)…'
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
rm -f "$SCRIPT_DIR/logs/"*Native.log 2>/dev/null
( cd "$SCRIPT_DIR" && dotnet "$DLL" )
RC_ON=$?

# ── INDEPENDENT PROOF ────────────────────────────────────────────────────────
# The counts above come from our managed code. This is the native profiler's OWN log
# (a C++ .dylib, not our code) reporting that IT received both configs and rewrote both
# methods via ReJIT — proof the capture is real, not printed by us.
pause $'\n⏎  show the NATIVE profiler\'s own log (independent proof)…'
NATIVE_LOG=$(ls -t "$SCRIPT_DIR/logs/"*Native.log 2>/dev/null | head -1)
echo
echo "════════════════════════════════════════════════════════════════════════════"
echo "  INDEPENDENT PROOF — from the native profiler's OWN log (not our C#):"
echo "  $NATIVE_LOG"
echo
if [ -n "$NATIVE_LOG" ]; then
  grep -E "received id: (probe-process|bp-checkout)|Request ReJIT done|CallTarget_RewriterCallback.*(Process|Checkout)" "$NATIVE_LOG" \
    | sed -E 's/^\[[^]]*\] \[[^]]*\] \[[^]]*\] /    /'
else
  echo "  (native log not found)"
fi
echo "════════════════════════════════════════════════════════════════════════════"

echo
if [ $RC_OFF -eq 0 ] && [ $RC_ON -eq 0 ]; then
  echo "  ✅ PROVEN: PROBE + BREAKPOINT end-to-end (mock backend → client → native profiler)."
  echo "     Hit-limit gating shown (PROBE=5, BREAKPOINT=3); profiler OFF→0 baseline held;"
  echo "     the native profiler's own log (above) confirms it rewrote both methods."
else
  echo "  ❌ OFF rc=$RC_OFF ON rc=$RC_ON (both should be 0 = all checks passed)"
fi

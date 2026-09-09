#!/bin/bash
# TIMING harness for Approach A (forked profiler + ReJIT line probes).
# Runs NATIVELY on the host (macOS arm64) against the forked native dylib, so the timing numbers are
# not distorted by the colima VM's virtualized clock/scheduler.
#
# Mirrors poc/LineProbeGatedE2E/run.sh exactly for the profiler wiring; the only difference is the
# project and the extra timing knobs (LINEPROBE_APPLY_COUNT, LINEPROBE_GAP_US).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

FORK_DYLIB="${DI_PROFILER_OVERRIDE:-$REPO_ROOT/src/OpenTelemetry.AutoInstrumentation.Native/build/bin/OpenTelemetry.AutoInstrumentation.Native.dylib}"

[ -d "$DISTRO_DIR" ] || { echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"; exit 1; }
[ -f "$FORK_DYLIB" ] || { echo "ERROR: forked profiler not built: $FORK_DYLIB"; exit 1; }
echo "Using FORKED native profiler: $FORK_DYLIB"

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$FORK_DYLIB"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"

# Keep profiler logging at INFO. At `debug` the native logger writes a line per ModuleLoad/ReJIT,
# and that file I/O lands INSIDE the apply window we are trying to measure.
export OTEL_LOG_LEVEL="${OTEL_LOG_LEVEL:-info}"

# Interior statement boundary B (`return y;`) in every generated target body.
export LINEPROBE_IL_OFFSET="${LINEPROBE_IL_OFFSET:-5}"
export LINEPROBE_APPLY_COUNT="${LINEPROBE_APPLY_COUNT:-40}"
export LINEPROBE_GAP_US="${LINEPROBE_GAP_US:-20}"

# Deterministic timing conditions: no tiered-compilation promotion mid-measurement.
export DOTNET_TieredPGO=0

mkdir -p "$SCRIPT_DIR/logs"
cd "$SCRIPT_DIR"
exec dotnet run -c Debug --framework net8.0

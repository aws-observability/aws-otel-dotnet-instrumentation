#!/bin/bash
# Phase-2 line-probe PoC (AWS Distro DI). Clone of poc/ProfilerE2E/run-e2e.sh, but loads the
# FORKED native profiler (with the AddLineProbes export) instead of the shipped one.
#
# The offset is HARDCODED here (no PdbReader — see BRIEF). IL_0005 is statement boundary B
# (`return y;`) in SpikeTarget.Compute, i.e. just before `ldloc.0; ret`. It was obtained by reading
# the portable-PDB sequence points (dotnet-script over System.Reflection.Metadata) and cross-checked
# against `ilspycmd --il-sequence-points`. See DI-LINE-LEVEL-PHASE2-POC-RESULTS.md.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

# The forked native profiler binary built from poc/fork/otel-dotnet-fork @ b61301e + the line-probe delta.
FORK_DYLIB="${DI_PROFILER_OVERRIDE:-$REPO_ROOT/src/OpenTelemetry.AutoInstrumentation.Native/build/bin/OpenTelemetry.AutoInstrumentation.Native.dylib}"

if [ ! -d "$DISTRO_DIR" ]; then
    echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"; exit 1
fi
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
export OTEL_LOG_LEVEL="debug"

# The one hardcoded interior IL offset (statement boundary B). Overridable for the fail-safe test.
export LINEPROBE_IL_OFFSET="${LINEPROBE_IL_OFFSET:-5}"

cd "$SCRIPT_DIR"
dotnet run -c Debug --framework net8.0

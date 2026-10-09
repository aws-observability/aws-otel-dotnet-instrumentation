#!/bin/bash
# BOX-GATE spike (DECISION A). Loads the FORKED native profiler (with the AddLineProbes export
# extended for the two-call GATED emission) and runs two hot targets: one woven with the gated
# `ldc;call ShouldCapture;brfalse SKIP;ldc;ldc;box;call Capture` sequence, one with the ungated
# always-box sequence. Proves the interior brfalse+skip is a valid body and that the skip avoids the
# per-hit box allocation on the discard path.
#
# The offset is HARDCODED (no PdbReader). IL_0005 is statement boundary B (`return y;`) in both
# GatedSpikeTarget.HotGated and .HotUngated — identical IL to the proven Phase-2 SpikeTarget.
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
export OTEL_LOG_LEVEL="debug"

# Interior statement boundary B. Overridable for a fail-safe sanity check.
export LINEPROBE_IL_OFFSET="${LINEPROBE_IL_OFFSET:-5}"

cd "$SCRIPT_DIR"
dotnet run -c Debug --framework net8.0

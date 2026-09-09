#!/bin/bash
# R9 (removal-under-load / teardown-vs-in-flight) + gap-2/3 (>2 probes, MIXED emission modes) E2E.
# Loads the FORKED native profiler (with AddLineProbes + RemoveLineProbe) — same wiring as
# poc/N2MultiProbeE2E/run.sh, which is the proven template.
#
# Offsets are NOT hardcoded here: the harness reads its own portable PDB's sequence points at
# runtime to discover real statement boundaries (the production PdbReader mechanism).
#
# Controls:
#   ./run.sh                     positive run  — removal must silence the removed probe
#   R9_NO_REMOVE=1 ./run.sh      NEG-1: skip RemoveLineProbe; the silencing must DISAPPEAR
#   R9_BAD_OFFSETS=1 ./run.sh    NEG-2: mid-instruction offsets; injection must be refused
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

FORK_DYLIB="${DI_PROFILER_OVERRIDE:-$REPO_ROOT/src/OpenTelemetry.AutoInstrumentation.Native/build/bin/OpenTelemetry.AutoInstrumentation.Native.dylib}"

if [ ! -d "$DISTRO_DIR" ]; then
    echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"; exit 1
fi
[ -f "$FORK_DYLIB" ] || { echo "ERROR: forked profiler not built: $FORK_DYLIB"; exit 1; }
echo "Using FORKED native profiler: $FORK_DYLIB"

# Fail loudly if the fork predates the removal work — otherwise RemoveLineProbe silently no-ops via
# a missing-entrypoint path and the R9 result would be a false negative.
# NB: do NOT pipe into `grep -q` here — it exits on first match, SIGPIPEs nm, and under `pipefail`
# the pipeline reports failure even though the symbol WAS found (this bit once already).
FORK_EXPORTS="$(nm -gU "$FORK_DYLIB" 2>/dev/null || true)"
case "$FORK_EXPORTS" in
    *_RemoveLineProbe*) ;;
    *) echo "ERROR: this dylib does not export RemoveLineProbe — rebuild the fork."; exit 1 ;;
esac
case "$FORK_EXPORTS" in
    *_AddLineProbes*) ;;
    *) echo "ERROR: this dylib does not export AddLineProbes — rebuild the fork."; exit 1 ;;
esac
echo "Fork exports verified: AddLineProbes + RemoveLineProbe"

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$FORK_DYLIB"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
export OTEL_LOG_LEVEL="debug"

# Tiered PGO off so the JIT doesn't reshape the target between warm-up and measurement.
export DOTNET_TieredPGO=0

rm -rf "$SCRIPT_DIR/logs"; mkdir -p "$SCRIPT_DIR/logs"

cd "$SCRIPT_DIR"
dotnet run -c Debug --framework net8.0

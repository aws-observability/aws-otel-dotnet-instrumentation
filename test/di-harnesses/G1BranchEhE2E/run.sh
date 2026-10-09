#!/bin/bash
# G1 gate (risk R2: branch + EH relocation on interior IL injection). Loads the FORKED native
# profiler and drives FOUR cases, ONE per process (ReJIT is triggered by AddLineProbes):
#   sum           : forward + backward branch relocation (inject in a loop body)   -> assertions 1,2,3
#   guarded_try   : EH try-region relocation (inject inside try)                    -> assertions 1,4,3
#   guarded_catch : EH catch-region survives offset shift (DivideByZero caught)     -> assertions 1,5,3
#   try_entry     : SAFETY — inject at try-entry offset is REFUSED (body intact)     -> assertion  6
#
# All IL offsets are HARDCODED (no PdbReader), obtained by `ilspycmd -il` + portable-PDB sequence
# points on THIS built assembly (see DI-LINE-LEVEL-G1-SPIKE-RESULTS.md §"How offsets were obtained").
# Overall exit code = total FAILED assertions across all cases.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

FORK_DYLIB="${DI_PROFILER_OVERRIDE:-$REPO_ROOT/src/OpenTelemetry.AutoInstrumentation.Native/build/bin/OpenTelemetry.AutoInstrumentation.Native.dylib}"

[ -d "$DISTRO_DIR" ] || { echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"; exit 1; }
[ -f "$FORK_DYLIB" ] || { echo "ERROR: forked profiler not built: $FORK_DYLIB"; exit 1; }
echo "Using FORKED native profiler: $FORK_DYLIB"
echo

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$FORK_DYLIB"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
export OTEL_LOG_LEVEL="debug"

cd "$SCRIPT_DIR"

# Build once; then each case is a fresh process (`dotnet exec` on the built DLL, no rebuild).
dotnet build -c Debug --framework net8.0 -v quiet >/dev/null || { echo "BUILD FAILED"; exit 1; }
DLL="$SCRIPT_DIR/bin/Debug/net8.0/G1BranchEhE2E.dll"

CASES=("${G1_CASES:-sum guarded_try guarded_catch try_entry}")
TOTAL_FAIL=0
for c in ${CASES[@]}; do
    echo "==================================================================="
    echo ">>> CASE: $c"
    echo "==================================================================="
    G1_CASE="$c" dotnet exec "$DLL"
    rc=$?
    TOTAL_FAIL=$((TOTAL_FAIL + rc))
    echo
done

echo "==================================================================="
echo ">>> G1 TOTAL FAILED ASSERTIONS across all cases: $TOTAL_FAIL"
echo "==================================================================="
exit $TOTAL_FAIL

#!/bin/bash
# ASYNC line-probe spike (DECISION B core). Loads the FORKED native profiler (with the AddLineProbes
# export extended for hoisted-field capture) and runs an async target whose local `y` is hoisted
# onto the compiler-generated state machine and survives an `await`.
#
# HARDCODED (no PdbReader), all obtained by inspecting THIS built assembly (see ASYNC-SPIKE-RESULTS):
#   - target type   : AsyncSpikeTarget+<Compute>d__0   (nested state machine; '+' one-level nesting)
#   - target method : MoveNext
#   - IL offset      : 125 (0x7D)  -> L19 `int z = y*2;`, POST-await statement boundary, empty stack
#   - hoisted field  : 0x04000013  -> mdFieldDef of `<y>5__1` (int32) in this module
# Overridable via ASYNC_IL_OFFSET / ASYNC_HOISTED_FIELD_TOKEN for the negative (pre-assignment) case.
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

# Positive site by default. For the negative (read-timing hazard) case, override:
#   ASYNC_IL_OFFSET=15   (0x0F, L17 `int y = x+1;`, BEFORE the `stfld <y>5__1` at IL_0018)
#     -> probe fires but reads the hoisted field's DEFAULT (0), documenting the pre-assignment hazard.
#   (ASYNC_IL_OFFSET=14 / 0x0E is a branch TARGET; injecting there is skipped by the incoming br.s —
#    a separate relocation caveat, not the read-timing negative.)
export ASYNC_IL_OFFSET="${ASYNC_IL_OFFSET:-125}"
# NOT DEFAULTED HERE ANY MORE. This export pinned 0x04000013 — `<>t__builder`, not `y` — and it OVERRODE
# the corrected default inside Program.cs, so the probe silently boxed the builder's first four bytes
# (913593088) instead of 42. Program.cs now RESOLVES the token by reflecting the built assembly; leave this
# unset unless you are deliberately driving the negative (wrong-token) case.
if [ -n "${ASYNC_HOISTED_FIELD_TOKEN:-}" ]; then export ASYNC_HOISTED_FIELD_TOKEN; fi

cd "$SCRIPT_DIR"
dotnet run -c Debug --framework net8.0

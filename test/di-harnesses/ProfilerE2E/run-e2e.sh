#!/bin/bash
# Run the DI Profiler E2E spike with the real OTel native profiler loaded.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

if [ ! -d "$DISTRO_DIR" ]; then
    echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"; exit 1
fi

OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)
case "$ARCH" in x86_64) ARCH="x64" ;; aarch64|arm64) ARCH="arm64" ;; esac
case "$OS" in
    linux)  PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so" ;;
    darwin) PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib" ;;
    *) echo "ERROR: Unsupported OS: $OS"; exit 1 ;;
esac
[ -f "$PROFILER_PATH" ] || { echo "ERROR: profiler not found: $PROFILER_PATH"; exit 1; }
echo "Using native profiler: $PROFILER_PATH"

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
# The AWS plugin is what drives full managed-profiler initialization (loads
# OpenTelemetry.AutoInstrumentation into the AppDomain). Without it the native rewriter SKIPS every weave
# with "managed profiler has not yet been loaded" — exactly GA's OTEL_DOTNET_AUTO_PLUGINS from instrument.sh.
# DI itself is enabled + initialized from Program.cs (after the mock backend is up), so the plugin's own
# early DI-init sees ENABLED unset and skips it — avoiding a double init against a not-yet-listening backend.
export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
# Surface profiler diagnostics so a silent no-weave is visible.
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$SCRIPT_DIR/logs"
export OTEL_LOG_LEVEL="debug"

cd "$SCRIPT_DIR"
dotnet run -c Release --framework net8.0

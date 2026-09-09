#!/bin/bash
# Run the CallTarget POC with the OTel native profiler loaded.
# Requires the distribution to be built first: cd ../../ && bash build.sh
#
# This script sets up the environment variables needed for the CLR to load
# the native profiler, which is what enables CallTarget IL rewriting.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"

if [ ! -d "$DISTRO_DIR" ]; then
    echo "ERROR: OpenTelemetryDistribution not found at $DISTRO_DIR"
    echo ""
    echo "Build the distribution first:"
    echo "  cd $REPO_ROOT && bash build.sh"
    echo ""
    echo "Running in simulation mode instead..."
    echo ""
    cd "$SCRIPT_DIR" && dotnet run
    exit 0
fi

# Detect OS and architecture
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)
case "$ARCH" in
    x86_64) ARCH="x64" ;;
    aarch64|arm64) ARCH="arm64" ;;
esac

# Set profiler path based on OS
case "$OS" in
    linux)
        PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so"
        ;;
    darwin)
        PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib"
        ;;
    *)
        echo "ERROR: Unsupported OS: $OS"
        exit 1
        ;;
esac

if [ ! -f "$PROFILER_PATH" ]; then
    echo "ERROR: Native profiler not found at: $PROFILER_PATH"
    echo "Running in simulation mode..."
    cd "$SCRIPT_DIR" && dotnet run
    exit 0
fi

echo "Using native profiler: $PROFILER_PATH"
echo ""

# Configure profiler environment
export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"

# Run the POC
cd "$SCRIPT_DIR" && dotnet run

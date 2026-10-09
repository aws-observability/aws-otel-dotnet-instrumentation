#!/bin/bash
# ============================================================================
#  BULK APPLY measurement — host half. Mirrors run-e2e-linux.sh exactly, but
#  runs run-bulk-incontainer.sh instead of the 5-check E2E.
#
#  Usage:  ./run-bulk-linux.sh            # N=20 probes
#          DI_BULK_METHODS=10 ./run-bulk-linux.sh
#          SKIP_BUILD=1 ./run-bulk-linux.sh
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
ARCH=$(uname -m); case "$ARCH" in aarch64|arm64) DARCH="arm64";; x86_64) DARCH="x64";; esac
DISTRO="$SCRIPT_DIR/OpenTelemetryDistribution-linux-$DARCH"

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "[host] building current DI dll + SampleApp + MockBackend…"
  DI_PROJ="$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/AWS.Distro.OpenTelemetry.DynamicInstrumentation.csproj"
  ( cd "$REPO_ROOT" && dotnet build "$DI_PROJ" -c Release -f net8.0 -v q --nologo ) >/dev/null || { echo "DI build failed"; exit 99; }
  ( cd "$SCRIPT_DIR/SampleApp"  && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "app build failed"; exit 99; }
  ( cd "$SCRIPT_DIR/MockBackend" && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "mock build failed"; exit 99; }
  cp "$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/bin/Release/net8.0/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" \
     "$DISTRO/net/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" || { echo "DI dll refresh failed"; exit 99; }
  echo "[host] refreshed Linux distro DI dll from current source"
fi

[ -f "$DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so" ] || {
  echo "ERROR: Linux native profiler missing: $DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so"; exit 99; }

echo "[host] launching BULK APPLY measurement inside $IMAGE ($DARCH)…"
docker run --rm \
  -e DARCH="$DARCH" \
  -e DI_BULK_METHODS="${DI_BULK_METHODS:-20}" \
  -v "$SCRIPT_DIR:/demo" \
  -w /demo \
  "$IMAGE" bash /demo/run-bulk-incontainer.sh
echo "exit=$?"

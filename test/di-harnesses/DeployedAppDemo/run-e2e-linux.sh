#!/bin/bash
# ============================================================================
#  .NET DI — DEPLOYED-APP E2E, LINUX CONTAINER edition
#
#  Runs the SAME real enable path as run-demo.sh, but inside a glibc .NET
#  container so it matches the fleet's Linux DI E2E environment and sidesteps
#  the macOS-only /var/log/opentelemetry blocker (§4.2 of DI-PR3-E2E-HANDOFF.md).
#
#  Host side (this script): build the current DI dll + SampleApp + MockBackend,
#  assemble a Linux-arm64 AWS distribution (AWS managed dlls are arch-independent;
#  only the native profiler .so is swapped for the upstream v1.16.0 Linux build —
#  exactly what AWS ships), then launch a container that runs the in-container
#  half below.
#
#  Container side: start the mock backend, create a probe + a breakpoint, enable
#  DI on the UNMODIFIED app purely via the GA env vars (mirroring instrument.sh),
#  run it, wait for the native ReJIT weave then the OTLP snapshot, and assert the
#  same 5-check contract as run-demo.sh. exit = number of failed checks.
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
ARCH=$(uname -m); case "$ARCH" in aarch64|arm64) DARCH="arm64";; x86_64) DARCH="x64";; esac
DISTRO="$SCRIPT_DIR/OpenTelemetryDistribution-linux-$DARCH"

# ── HOST: build current DI + apps, assemble the Linux distro ────────────────
if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "[host] building current DI dll + SampleApp + MockBackend…"
  DI_PROJ="$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/AWS.Distro.OpenTelemetry.DynamicInstrumentation.csproj"
  ( cd "$REPO_ROOT" && dotnet build "$DI_PROJ" -c Release -f net8.0 -v q --nologo ) >/dev/null || { echo "DI build failed"; exit 99; }
  ( cd "$SCRIPT_DIR/SampleApp"  && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "app build failed"; exit 99; }
  ( cd "$SCRIPT_DIR/MockBackend" && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "mock build failed"; exit 99; }
  # refresh the DI dll inside the Linux distro from the just-built source
  cp "$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/bin/Release/net8.0/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" \
     "$DISTRO/net/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" || { echo "DI dll refresh failed"; exit 99; }
  echo "[host] refreshed Linux distro DI dll from current source"
fi
[ -f "$DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so" ] || {
  echo "ERROR: Linux native profiler missing: $DISTRO/linux-$DARCH/OpenTelemetry.AutoInstrumentation.Native.so"; exit 99; }

# Beta mode (opt-in): when DI_BETA_ENDPOINT is set, forward the config leg to real beta from INSIDE the
# container by passing the endpoint + AWS creds through to the mock (which SigV4-signs). Snapshots stay
# local so the 5-check contract still asserts the captured body. This gives the FULL loop — real-beta
# config AND the weave+snapshot legs — in the Linux env where the weave actually fires.
BETA_ENV=()
if [ -n "${DI_BETA_ENDPOINT:-}" ]; then
  echo "[host] BETA MODE: forwarding config leg → $DI_BETA_ENDPOINT (passing AWS creds into container)"
  BETA_ENV+=( -e DI_BETA_ENDPOINT="$DI_BETA_ENDPOINT" )
  BETA_ENV+=( -e DI_BETA_REGION="${DI_BETA_REGION:-us-west-2}" )
  BETA_ENV+=( -e DI_BETA_SERVICE="${DI_BETA_SERVICE:-application-signals}" )
  BETA_ENV+=( -e AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-}" )
  BETA_ENV+=( -e AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-}" )
  BETA_ENV+=( -e AWS_SESSION_TOKEN="${AWS_SESSION_TOKEN:-}" )
  BETA_ENV+=( -e AWS_REGION="${AWS_REGION:-us-west-2}" )
fi

echo "[host] launching E2E inside $IMAGE ($DARCH)…"
docker run --rm \
  -e DARCH="$DARCH" \
  ${BETA_ENV[@]+"${BETA_ENV[@]}"} \
  -v "$SCRIPT_DIR:/demo" \
  -w /demo \
  "$IMAGE" bash /demo/run-e2e-incontainer.sh
echo "exit=$?"

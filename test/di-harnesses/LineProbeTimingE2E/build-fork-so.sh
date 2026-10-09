#!/bin/bash
# Build the FORKED native profiler as a linux-arm64 .so inside a Debian container.
# The fork already vendors coreclr PAL headers + spdlog (lib/), so we only need cmake+clang+make.
# Output: <fork>/src/OpenTelemetry.AutoInstrumentation.Native/build-linux/bin/OpenTelemetry.AutoInstrumentation.Native.so
set -euo pipefail
FORK_NATIVE=/fork/src/OpenTelemetry.AutoInstrumentation.Native
BUILD=$FORK_NATIVE/build-linux
rm -rf "$BUILD"; mkdir -p "$BUILD"
cd "$BUILD"
echo "[build] cmake configure (linux-arm64)…"
cmake ../ -DCMAKE_BUILD_TYPE=Release \
  -DOTEL_AUTO_VERSION=1.16.0 -DOTEL_AUTO_VERSION_MAJOR=1 -DOTEL_AUTO_VERSION_MINOR=16 -DOTEL_AUTO_VERSION_PATCH=0
echo "[build] make…"
make -j"$(nproc)"
echo "[build] result:"
ls -la "$BUILD/bin/"
file "$BUILD/bin/OpenTelemetry.AutoInstrumentation.Native.so" || true

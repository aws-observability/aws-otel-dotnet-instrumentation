#!/bin/bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

check_if_step_failed_and_exit() {
  if [ $? -ne 0 ]; then
    echo "$1"
    exit 1
  fi
}

# Build distro
cd .. || exit 1

# Compile the VENDORED native profiler first, so the distribution the contract tests install actually
# supports line-level Dynamic Instrumentation.
#
# WHY THIS IS A SEPARATE, EXPLICIT STEP. CompileNativeProfiler is deliberately NOT part of the default
# Workflow (it would make cmake a hard requirement of every build), and the swap into the distribution is
# conditional on a built library existing on disk. So without this line the swap silently no-ops and the
# distribution ships the STOCK upstream profiler — which has no AddLineProbes export. The managed side
# treats that as a normal runtime condition (ProfilerMissingLineProbeSupport), so line-level would simply
# never fire and no test would say why. Verified: before this step, test/dist's native library had no
# AddLineProbes symbol.
#
# NON-FATAL BY DESIGN. Contract tests did not previously need cmake, and method-level DI plus every
# non-DI contract test works fine on the stock profiler. So a missing cmake degrades to "line-level tests
# skip" rather than breaking the whole suite — the DI contract test detects the missing export and skips
# with that reason instead of failing for a deployment condition.
if command -v cmake >/dev/null 2>&1; then
  bash build.sh CompileNativeProfiler
  check_if_step_failed_and_exit "There was an error building the vendored native profiler, exiting"
else
  echo "WARNING: cmake not found on PATH."
  echo "  The distribution will ship the STOCK upstream native profiler, which does not export"
  echo "  AddLineProbes. Dynamic Instrumentation LINE-LEVEL contract tests will SKIP (method-level is"
  echo "  unaffected). Install cmake to exercise line-level."
fi

bash build.sh
check_if_step_failed_and_exit "There was an error building AWS Otel DotNet, exiting"

cd test || exit 1
# Clear the PREVIOUS copy before installing the new one.
#
# This deliberately removes ./dist/OpenTelemetryDistribution, which is where the copy below actually lands.
# The old line removed ./OpenTelemetryDistribution — a path that does not exist inside test/ — so it deleted
# nothing and `cp -r` MERGED into the stale tree. Consequence, measured on this machine: dist carried
# linux-x64 and linux-arm64 folders left over from an earlier build even though the current build produced
# only osx-arm64, and a contract-test image would happily load that stale native library. Files a build no
# longer produces have to disappear, or the installed distribution is a mix of several builds.
rm -rf ./dist/OpenTelemetryDistribution
mkdir -p ./dist
cp -r ../OpenTelemetryDistribution ./dist
check_if_step_failed_and_exit "There was an error moving OpenTelemetryDistribution to the sample app , exiting"

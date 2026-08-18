#!/bin/bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

check_if_step_failed_and_exit() {
  if [ $? -ne 0 ]; then
    echo $1
    exit 1
  fi
}

# Build distro
cd ..
bash build.sh
check_if_step_failed_and_exit "There was an error building AWS Otel DotNet, exiting"

cd test
rm -rf ./OpenTelemetryDistribution
mkdir -p ./dist
cp -r ../OpenTelemetryDistribution ./dist
check_if_step_failed_and_exit "There was an error moving OpenTelemetryDistribution to the sample app , exiting"

rm -rf ./dist/nuget
mkdir -p ./dist/nuget

dotnet pack ../src/AWS.OpenTelemetry.CloudWatch.Plugin/AWS.OpenTelemetry.CloudWatch.Plugin.csproj \
  --configuration Release \
  --output ./dist/nuget
check_if_step_failed_and_exit "There was an error packing the CloudWatch plugin, exiting"

dotnet pack ../src/OpenTelemetry.Instrumentation.AWS/OpenTelemetry.Instrumentation.AWS.csproj \
  --configuration Release \
  --output ./dist/nuget
check_if_step_failed_and_exit "There was an error packing the AWS instrumentation, exiting"

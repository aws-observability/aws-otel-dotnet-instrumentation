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

#!/usr/bin/env bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

check_if_step_failed_and_exit() {
  if [ $? -ne 0 ]; then
    echo $1
    exit 1
  fi
}

# Build the distro
bash ../../build.sh
check_if_step_failed_and_exit "There was an error building AWS Otel DotNet, exiting"

rm -rf ./OpenTelemetryDistribution
cp -r ../../OpenTelemetryDistribution .
check_if_step_failed_and_exit "There was an error moving OpenTelemetryDistribution to the sample app , exiting"

docker build -t aspnetapp .
check_if_step_failed_and_exit "There was an error building the docker container, exiting"

docker compose up 
check_if_step_failed_and_exit "There was an error starting up the sample app, exiting"
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
cd ../../../
bash ./build.sh
check_if_step_failed_and_exit "There was an error building AWS Otel DotNet, exiting"

cd ./sample-applications/lambda-test-apps/SimpleLambdaFunction
dotnet lambda package -pl ./src/SimpleLambdaFunction
check_if_step_failed_and_exit "There was an error building the SimpleLambdaFunction, exiting"

cd ./terraform/lambda
terraform init
terraform apply -auto-approve
check_if_step_failed_and_exit "There was an error deploying the SimpleLambdaFunction, exiting"
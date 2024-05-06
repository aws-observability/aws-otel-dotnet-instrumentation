#!/bin/sh
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

rm -rf ./OpenTelemetryDistribution
cp -r ../../OpenTelemetryDistribution .

docker build -t aspnetapp .

docker-compose up 
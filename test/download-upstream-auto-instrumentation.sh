#!/bin/bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <version, for example v1.16.0>"
  exit 1
fi

version="$1"
archive="opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip"
download_url="https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/${version}/${archive}"
destination="./dist/UpstreamOpenTelemetryDistribution"
archive_path="./dist/${archive}"

mkdir -p ./dist
rm -rf "$destination"
curl --fail --location --retry 3 --output "$archive_path" "$download_url"
unzip -q "$archive_path" -d "$destination"
rm "$archive_path"

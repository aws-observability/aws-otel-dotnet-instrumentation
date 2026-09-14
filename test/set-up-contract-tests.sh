#!/bin/bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

# Fail fast
set -e

# Check script is running in contract-tests
current_path=$(pwd)
current_dir="${current_path##*/}"
if [ "$current_dir" != "test" ]; then
  echo "Please run from test dir"
  exit 1
fi

applications=("$@")
for application in "${applications[@]}"; do
  if [ ! -d "contract-tests/images/applications/${application}" ]; then
    echo "Unknown contract-test application: ${application}"
    exit 1
  fi
done

cloudwatch_plugin_selected=false
if [ "${#applications[@]}" -eq 0 ]; then
  cloudwatch_plugin_selected=true
else
  for application in "${applications[@]}"; do
    if [ "$application" = "cloudwatch-plugin-otel" ]; then
      cloudwatch_plugin_selected=true
      break
    fi
  done
fi

# Remove old whl files (excluding distro whl)
rm -rf dist/mock_collector*
rm -rf dist/contract_tests*

if [ "$cloudwatch_plugin_selected" = true ]; then
  plugin_project="../src/AWS.OpenTelemetry.CloudWatch.Plugin/AWS.OpenTelemetry.CloudWatch.Plugin.csproj"
  aws_instrumentation_project="../src/OpenTelemetry.Instrumentation.AWS/OpenTelemetry.Instrumentation.AWS.csproj"
  otel_version="${OTEL_VERSION:-1.16.0}"
  otel_auto_instrumentation_version="${OTEL_AUTO_INSTRUMENTATION_VERSION:-1.16.0}"
  otel_instrumentation_version="${OTEL_INSTRUMENTATION_VERSION:-1.16.0}"
  otel_auto_instrumentation_tag="v${otel_auto_instrumentation_version#v}"
  otel_auto_download_dir="$(mktemp -d)"
  otel_auto_installer="${otel_auto_download_dir}/otel-dotnet-auto-install.sh"
  otel_auto_installer_url="https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/${otel_auto_instrumentation_tag}/otel-dotnet-auto-install.sh"

  mkdir -p ./dist
  curl --fail --location --retry 3 --output "$otel_auto_installer" "$otel_auto_installer_url"
  OS_TYPE=linux-glibc \
    ARCHITECTURE=x64 \
    VERSION="$otel_auto_instrumentation_tag" \
    OTEL_DOTNET_AUTO_HOME="$(pwd)/dist/UpstreamOpenTelemetryDistribution" \
    DOWNLOAD_DIR="$otel_auto_download_dir" \
    sh "$otel_auto_installer"
  rm -rf "$otel_auto_download_dir"

  rm -rf ./dist/nuget
  mkdir -p ./dist/nuget
  dotnet pack "$plugin_project" --configuration Release --output ./dist/nuget
  dotnet pack "$aws_instrumentation_project" --configuration Release --output ./dist/nuget

  shopt -s nullglob
  cloudwatch_plugin_packages=(./dist/nuget/AWS.OpenTelemetry.CloudWatchPluginOtel.*.nupkg)
  aws_instrumentation_packages=(./dist/nuget/OpenTelemetry.Instrumentation.AWS.*.nupkg)
  shopt -u nullglob
  if [ "${#cloudwatch_plugin_packages[@]}" -ne 1 ]; then
    echo "Expected exactly one CloudWatch plugin package in test/dist/nuget"
    exit 1
  fi
  if [ "${#aws_instrumentation_packages[@]}" -ne 1 ]; then
    echo "Expected exactly one AWS instrumentation package in test/dist/nuget"
    exit 1
  fi

  cloudwatch_plugin_package="${cloudwatch_plugin_packages[0]##*/}"
  cloudwatch_plugin_version="${cloudwatch_plugin_package#AWS.OpenTelemetry.CloudWatchPluginOtel.}"
  cloudwatch_plugin_version="${cloudwatch_plugin_version%.nupkg}"
  aws_instrumentation_package="${aws_instrumentation_packages[0]##*/}"
  aws_instrumentation_version="${aws_instrumentation_package#OpenTelemetry.Instrumentation.AWS.}"
  aws_instrumentation_version="${aws_instrumentation_version%.nupkg}"
fi

# Install python dependency for contract-test
pip3 install pymysql
pip3 install cryptography
pip3 install build pytest

# To be clear, install binary for psycopg2 have no negative influence on otel here
# since Otel-Instrumentation running in container that install psycopg2 from source
pip3 install sqlalchemy psycopg2-binary

# Create mock-collector image
cd contract-tests/images/mock-collector
docker build . -t aws-application-signals-mock-collector || {
  echo "Docker build for mock collector failed"
  exit 1
}

# Create mock-di-api image — stands in for the local CloudWatch Agent that serves Dynamic Instrumentation
# probe configurations and receives status reports. Built by name rather than by the applications/* loop
# below, because it is a test double like mock-collector, not an instrumented application.
# Guarded so a missing directory cannot leave the build running from the previous one, which would tag the
# mock-collector image as the DI API and surface as unexplained "no snapshots".
cd ../mock-di-api || { echo "contract-tests/images/mock-di-api not found"; exit 1; }
docker build . -t aws-application-signals-mock-di-api || {
  echo "Docker build for mock DI api failed"
  exit 1
}

# Create application images
cd ../../..
applications_built=0
for dir in contract-tests/images/applications/*
do
  application_directory="${dir##*/}"
  if [ "${#applications[@]}" -gt 0 ]; then
    application_included=false
    for included_application in "${applications[@]}"; do
      if [ "$application_directory" = "$included_application" ]; then
        application_included=true
        break
      fi
    done
    if [ "$application_included" = false ]; then
      continue
    fi
  fi

  application=$(echo "$application_directory" | tr '[:upper:]' '[:lower:]')
  echo "application: ${application}"
  if [ "$application_directory" = "cloudwatch-plugin-otel" ]; then
    docker build --platform linux/amd64 . \
      --build-arg "CLOUDWATCH_PLUGIN_VERSION=${cloudwatch_plugin_version}" \
      --build-arg "AWS_INSTRUMENTATION_VERSION=${aws_instrumentation_version}" \
      --build-arg "OTEL_VERSION=${otel_version}" \
      --build-arg "OTEL_AUTO_INSTRUMENTATION_VERSION=${otel_auto_instrumentation_version}" \
      --build-arg "OTEL_INSTRUMENTATION_VERSION=${otel_instrumentation_version}" \
      -t "aws-application-signals-tests-${application}-app" \
      -f "${dir}/Dockerfile" || {
      echo "Docker build for ${application} application failed"
      exit 1
    }
  else
    docker build . -t "aws-application-signals-tests-${application}-app" -f "${dir}/Dockerfile" || {
      echo "Docker build for ${application} application failed"
      exit 1
    }
  fi
  applications_built=$((applications_built + 1))
done

if [ "$applications_built" -eq 0 ]; then
  echo "No contract-test application images matched the configured filters"
  exit 1
fi

# Build and install mock-collector
cd contract-tests/images/mock-collector
python3 -m build --outdir ../../../dist
cd ../../../dist
pip3 install mock_collector-1.0.0-py3-none-any.whl --force-reinstall

# Build and install contract-tests
cd ../contract-tests/tests
python3 -m build --outdir ../../dist
cd ../../dist
# --force-reinstall causes `ERROR: No matching distribution found for mock-collector==1.0.0`, but uninstalling and reinstalling works pretty reliably.
pip3 uninstall contract-tests -y
pip3 install contract_tests-1.0.0-py3-none-any.whl

#!/bin/bash
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

# Fail fast
set -e

# # Set up service
# python3 manage.py migrate --noinput
# python3 manage.py collectstatic --noinput

export ASPNETCORE_URLS=http://+:8001
export LISTEN_ADDRESS=0.0.0.0:8001

# If a distro is not provided, run service normally. If it is, run the service with instrumentation.
if [[ -z "${DO_INSTRUMENT}" ]]; then
    # python3 manage.py runserver 0.0.0.0:$PORT --noreload
    unset CORECLR_ENABLE_PROFILING
    unset CORECLR_PROFILER
    unset CORECLR_PROFILER_PATH
    unset DOTNET_SHARED_STORE
    unset DOTNET_STARTUP_HOOKS
    unset OTEL_DOTNET_AUTO_HOME
    unset OTEL_DOTNET_AUTO_PLUGINS
    dotnet integration-test-app.dll
    # dotnet AppSignals.NetCore.dll
else
    # opentelemetry-instrument python3 manage.py runserver 0.0.0.0:$PORT --noreload
    dotnet integration-test-app.dll
    # dotnet AppSignals.NetCore.dll
fi

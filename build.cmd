:; Copyright The OpenTelemetry Authors
:; SPDX-License-Identifier: Apache-2.0
:; Modifications Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

:; set -eo pipefail
:; SCRIPT_DIR=$(cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd)
:; ${SCRIPT_DIR}/build.sh "$@"
:; exit $?

@ECHO OFF
powershell -ExecutionPolicy ByPass -NoProfile -File "%~dp0build.ps1" %*

# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""ServiceEvents contract tests against the .NET ServiceEvents.NetCore app.

Runs the full ServiceEventsContractTestBase suite (EndpointSummary,
service.function.duration histogram, IncidentSnapshot exception/latency/POST,
per-endpoint latency override, EndpointErrorMetrics `count`, DeploymentEvent,
incidents_exemplar) against the instrumented ASP.NET Core contract-test app.
"""
from typing_extensions import override

from amazon.serviceevents.serviceevents_contract_test_base import ServiceEventsContractTestBase


class DotnetServiceEventsTest(ServiceEventsContractTestBase):
    __test__ = True

    @override
    @staticmethod
    def get_application_image_name() -> str:
        return "aws-application-signals-tests-serviceevents.netcore-app"

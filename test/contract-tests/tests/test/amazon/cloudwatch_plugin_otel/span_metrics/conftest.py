# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

from logging import INFO, Logger, getLogger
from typing import Iterator

import pytest
from docker import DockerClient
from docker.models.networks import Network, NetworkCollection
from testcontainers.core.waiting_utils import wait_for_logs

from amazon.base.contract_test_base import NETWORK_NAME
from amazon.base.dependency_containers import (
    create_localstack_container,
    create_redis_container,
    log_and_stop_container,
)

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)


@pytest.fixture(scope="package", autouse=True)
def dependency_containers() -> Iterator[None]:
    network: Network = NetworkCollection(client=DockerClient()).create(NETWORK_NAME)
    local_stack = create_localstack_container(
        "localstack-cloudwatch-plugin-otel",
        ("s3", "sqs", "sns", "dynamodb"),
        "us-east-1",
    )
    redis = create_redis_container("redis-cloudwatch-plugin-otel")
    local_stack_started = False
    redis_started = False

    try:
        local_stack.start()
        local_stack_started = True
        redis.start()
        redis_started = True
        wait_for_logs(redis, "Ready to accept connections", timeout=30)
        yield
    finally:
        if redis_started:
            log_and_stop_container(redis, "Redis", _logger)
        if local_stack_started:
            log_and_stop_container(local_stack, "LocalStack", _logger)
        network.remove()

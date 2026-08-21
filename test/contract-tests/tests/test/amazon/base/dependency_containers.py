# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

from logging import Logger
from typing import Sequence

from docker.types import EndpointConfig
from testcontainers.core.container import DockerContainer
from testcontainers.localstack import LocalStackContainer

from amazon.base.contract_test_base import NETWORK_NAME


def create_localstack_container(
    name: str,
    services: Sequence[str],
    region: str,
    aliases: Sequence[str] = ("localstack", "s3.localstack"),
) -> LocalStackContainer:
    networking_config = {
        NETWORK_NAME: EndpointConfig(version="1.22", aliases=list(aliases))
    }
    return (
        LocalStackContainer(image="localstack/localstack:4.0.0")
        .with_name(name)
        .with_services(*services)
        .with_env("DEFAULT_REGION", region)
        .with_kwargs(network=NETWORK_NAME, networking_config=networking_config)
    )


def create_redis_container(name: str) -> DockerContainer:
    networking_config = {
        NETWORK_NAME: EndpointConfig(version="1.22", aliases=["redis"])
    }
    return (
        DockerContainer("redis:7")
        .with_name(name)
        .with_kwargs(network=NETWORK_NAME, networking_config=networking_config)
    )


def log_and_stop_container(
    container: DockerContainer,
    display_name: str,
    logger: Logger,
) -> None:
    stdout, stderr = container.get_logs()
    logger.info("%s stdout\n%s", display_name, stdout.decode())
    logger.info("%s stderr\n%s", display_name, stderr.decode())
    container.stop()

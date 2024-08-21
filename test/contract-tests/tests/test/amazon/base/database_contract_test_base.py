# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
from typing import Dict

from typing_extensions import override

from amazon.base.contract_test_base import ContractTestBase

AWS_REMOTE_DB_USER: str = "aws.remote.db.user"
DATABASE_HOST: str = "mydb"
DATABASE_NAME: str = "testdb"
DATABASE_PASSWORD: str = "example"
DATABASE_USER: str = "root"
SPAN_KIND_CLIENT: str = "CLIENT"
SPAN_KIND_LOCAL_ROOT: str = "LOCAL_ROOT"


class DatabaseContractTestBase(ContractTestBase):
    @staticmethod
    def get_remote_service() -> str:
        return None

    @staticmethod
    def get_database_port() -> int:
        return None

    def get_remote_resource_identifier(self) -> str:
        return f"{DATABASE_NAME}|{DATABASE_HOST}"

    @override
    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {
            "DB_HOST": DATABASE_HOST,
            "DB_USER": DATABASE_USER,
            "DB_PASS": DATABASE_PASSWORD,
            "DB_NAME": DATABASE_NAME,
        }

    def assert_drop_table_succeeds(self) -> None:
        self.mock_collector_client.clear_signals()
        self.do_test_requests("/drop_table", "GET", 200, 0, 0, sql_command="DROP TABLE")

    def assert_create_item_succeeds(self) -> None:
        self.mock_collector_client.clear_signals()
        self.do_test_requests("/create_item", "POST", 200, 0, 0, sql_command="INSERT INTO")

    def assert_select_succeeds(self) -> None:
        self.mock_collector_client.clear_signals()
        self.do_test_requests("/select", "GET", 200, 0, 0, sql_command="SELECT")

    def assert_fault(self) -> None:
        self.mock_collector_client.clear_signals()
        self.do_test_requests("/fault", "GET", 500, 0, 1, sql_command="SELECT DISTINCT")
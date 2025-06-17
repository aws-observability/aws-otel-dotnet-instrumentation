# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
from logging import INFO, Logger, getLogger
from typing import Dict, List
from docker.types import EndpointConfig
from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from testcontainers.localstack import LocalStackContainer
from typing_extensions import override

from amazon.base.contract_test_base import NETWORK_NAME, ContractTestBase
from amazon.utils.application_signals_constants import (
    AWS_LOCAL_SERVICE,
    AWS_REMOTE_CLOUDFORMATION_PRIMARY_IDENTIFIER,
    AWS_REMOTE_OPERATION,
    AWS_REMOTE_RESOURCE_ACCESS_KEY,
    AWS_REMOTE_RESOURCE_ACCOUNT_ID,
    AWS_REMOTE_RESOURCE_IDENTIFIER,
    AWS_REMOTE_RESOURCE_REGION,
    AWS_REMOTE_RESOURCE_TYPE,
    AWS_REMOTE_SERVICE,
    AWS_SPAN_KIND,
)
from opentelemetry.proto.common.v1.common_pb2 import AnyValue, KeyValue
from opentelemetry.proto.metrics.v1.metrics_pb2 import ExponentialHistogramDataPoint, Metric
from opentelemetry.proto.trace.v1.trace_pb2 import Span
from opentelemetry.semconv.trace import SpanAttributes

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)

_AWS_SQS_QUEUE_URL: str = "aws.queue_url"
_AWS_SQS_QUEUE_NAME: str = "aws.sqs.queue_name"
_AWS_KINESIS_STREAM_ARN: str = "aws.kinesis.stream.arn"
_AWS_KINESIS_STREAM_NAME: str = "aws.kinesis.stream_name"
_AWS_SECRETSMANAGER_SECRET_ARN: str = "aws.secretsmanager.secret.arn"
_AWS_SNS_TOPIC_ARN: str = "aws.sns.topic.arn"
_AWS_STEPFUNCTIONS_ACTIVITY_ARN: str = "aws.stepfunctions.activity.arn"
_AWS_STEPFUNCTIONS_STATE_MACHINE_ARN: str = "aws.stepfunctions.state_machine.arn"
_AWS_BEDROCK_GUARDRAIL_ARN: str = "aws.bedrock.guardrail.arn"
_AWS_BEDROCK_GUARDRAIL_ID: str = "aws.bedrock.guardrail.id"
_AWS_BEDROCK_AGENT_ID: str = "aws.bedrock.agent.id"
_AWS_BEDROCK_KNOWLEDGE_BASE_ID: str = "aws.bedrock.knowledge_base.id"
_AWS_BEDROCK_DATA_SOURCE_ID: str = "aws.bedrock.data_source.id"
_GEN_AI_SYSTEM: str = "gen_ai.system"
_GEN_AI_REQUEST_MODEL: str = "gen_ai.request.model"
_GEN_AI_REQUEST_TOP_P: str = "gen_ai.request.top_p"
_GEN_AI_REQUEST_TEMPERATURE: str = "gen_ai.request.temperature"
_GEN_AI_REQUEST_MAX_TOKENS: str = "gen_ai.request.max_tokens"
_GEN_AI_USAGE_INPUT_TOKENS: str = "gen_ai.usage.input_tokens"
_GEN_AI_USAGE_OUTPUT_TOKENS: str = "gen_ai.usage.output_tokens"
_GEN_AI_RESPONSE_FINISH_REASONS: str = "gen_ai.response.finish_reasons"
_AWS_DYNAMODB_TABLE_ARN: str = "aws.dynamodb.table.arn"

# pylint: disable=too-many-public-methods
class AWSSdkTest(ContractTestBase):
    _local_stack: LocalStackContainer

    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {
            "AWS_SDK_S3_ENDPOINT": "http://s3.localstack:4566",
            "AWS_SDK_ENDPOINT": "http://localstack:4566",
            "AWS_REGION": "us-west-2",
            "AWS_ACCESS_KEY_ID": "testcontainers-localstack",
            "AWS_SECRET_ACCESS_KEY": "testcontainers-localstack"
        }

    @override
    def get_application_network_aliases(self) -> List[str]:
        return ["error.test", "fault.test"]

    @override
    def get_application_image_name(self) -> str:
        # This is the smaple-app image name, pending change as we developing the new
        # AWS Test Sample Application.
        return "aws-application-signals-tests-testsimpleapp.awssdk.core-app"
    
    @override
    def get_application_wait_pattern(self) -> str:
        return "Content root path: /app"

    @classmethod
    @override
    def set_up_dependency_container(cls):
        local_stack_networking_config: Dict[str, EndpointConfig] = {
            NETWORK_NAME: EndpointConfig(
                version="1.22",
                aliases=[
                    "localstack",
                    "s3.localstack",
                ],
            )
        }
        cls._local_stack: LocalStackContainer = (
            LocalStackContainer(image="localstack/localstack:4.0.0")
            .with_name("localstack")
            .with_services("s3", "secretsmanager", "sns", "sqs", "stepfunctions", "dynamodb", "kinesis")
            .with_env("DEFAULT_REGION", "us-west-2")
            .with_kwargs(network=NETWORK_NAME, networking_config=local_stack_networking_config)
        )
        cls._local_stack.start()


    @classmethod
    @override
    def tear_down_dependency_container(cls):
        _logger.info("LocalStack stdout")
        _logger.info(cls._local_stack.get_logs()[0].decode())
        _logger.info("LocalStack stderr")
        _logger.info(cls._local_stack.get_logs()[1].decode())
        cls._local_stack.stop()

    def test_s3_create_bucket(self):
        self.do_test_requests(
            "s3/createbucket/create-bucket/test-bucket-name",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::S3",
            remote_operation="PutBucket",
            remote_resource_type="AWS::S3::Bucket",
            remote_resource_identifier="test-bucket-name",
            cloudformation_primary_identifier="test-bucket-name",
            request_response_specific_attributes={
                SpanAttributes.AWS_S3_BUCKET: "test-bucket-name",
            },
            span_name="S3.PutBucket",
        )

    def test_s3_create_object(self):
        self.do_test_requests(
            "s3/createobject/put-object/some-object/test-bucket-name",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::S3",
            remote_operation="PutObject",
            remote_resource_type="AWS::S3::Bucket",
            remote_resource_identifier="test-bucket-name",
            cloudformation_primary_identifier="test-bucket-name",
            request_response_specific_attributes={
                SpanAttributes.AWS_S3_BUCKET: "test-bucket-name",
            },
            span_name="S3.PutObject",
        )

    def test_s3_delete_object(self):
        self.do_test_requests(
            "s3/deleteobject/delete-object/some-object/test-bucket-name",
            "GET",
            204,
            0,
            0,
            remote_service="AWS::S3",
            remote_operation="DeleteObject",
            remote_resource_type="AWS::S3::Bucket",
            remote_resource_identifier="test-bucket-name",
            cloudformation_primary_identifier="test-bucket-name",
            request_response_specific_attributes={
                SpanAttributes.AWS_S3_BUCKET: "test-bucket-name",
            },
            span_name="S3.DeleteObject",
        )

    def test_dynamodb_create_table(self):
        self.do_test_requests(
            "ddb/createtable/some-table",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::DynamoDB",
            remote_operation="CreateTable",
            remote_resource_type="AWS::DynamoDB::Table",
            remote_resource_identifier="test_table",
            cloudformation_primary_identifier="test_table",
            request_response_specific_attributes={
                # SpanAttributes.AWS_DYNAMODB_TABLE_NAMES: ["test_table"],
                "aws.table_name": ["test_table"],
            },
            span_name="DynamoDB.CreateTable",
        )

    def test_dynamodb_put_item(self):
        self.do_test_requests(
            "ddb/put-item/some-item",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::DynamoDB",
            remote_operation="PutItem",
            remote_resource_type="AWS::DynamoDB::Table",
            remote_resource_identifier="test_table",
            cloudformation_primary_identifier="test_table",
            request_response_specific_attributes={
                # SpanAttributes.AWS_DYNAMODB_TABLE_NAMES: ["test_table"],
                "aws.table_name": ["test_table"],
            },
            span_name="DynamoDB.PutItem",
        )

    def test_dynamodb_describe_table(self):
        self.do_test_requests(
            "ddb/describetable/some-table",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::DynamoDB",
            remote_operation="DescribeTable",
            remote_resource_type="AWS::DynamoDB::Table",
            remote_resource_identifier="test-table",
            remote_resource_account_id="000000000000",
            remote_resource_region="us-east-1",
            cloudformation_primary_identifier="test-table",
            request_response_specific_attributes={
                "aws.table_name": ["test-table"],
            },
            response_specific_attributes={
                _AWS_DYNAMODB_TABLE_ARN: r"arn:aws:dynamodb:us-east-1:000000000000:table/test-table",
            },
            span_name="DynamoDB.DescribeTable",
        )

    def test_sqs_create_queue(self):
        self.do_test_requests(
            "sqs/createqueue/some-queue",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::SQS",
            remote_operation="CreateQueue",
            remote_resource_type="AWS::SQS::Queue",
            remote_resource_identifier="test_queue",
            cloudformation_primary_identifier="http://sqs.us-east-1.localhost.localstack.cloud:4566/000000000000/test_queue",
            request_response_specific_attributes={
                _AWS_SQS_QUEUE_NAME: "test_queue",
                _AWS_SQS_QUEUE_URL: "http://sqs.us-east-1.localhost.localstack.cloud:4566/000000000000/test_queue",
            },
            span_name="SQS.CreateQueue",
        )

    def test_sqs_send_message(self):
        self.do_test_requests(
            "sqs/publishqueue/some-queue",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::SQS",
            remote_operation="SendMessage",
            remote_resource_type="AWS::SQS::Queue",
            remote_resource_identifier="test_queue",
            cloudformation_primary_identifier="http://sqs.us-east-1.localstack:4566/000000000000/test_queue",
            request_response_specific_attributes={
                _AWS_SQS_QUEUE_URL: "http://sqs.us-east-1.localstack:4566/000000000000/test_queue",
            },
            span_name="SQS.SendMessage",
        )

    def test_sqs_receive_message(self):
        self.do_test_requests(
            "sqs/consumequeue/some-queue",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::SQS",
            remote_operation="ReceiveMessage",
            remote_resource_type="AWS::SQS::Queue",
            remote_resource_identifier="test_queue",
            cloudformation_primary_identifier="http://sqs.us-east-1.localstack:4566/000000000000/test_queue",
            request_response_specific_attributes={
                _AWS_SQS_QUEUE_URL: "http://sqs.us-east-1.localstack:4566/000000000000/test_queue",
            },
            span_name="SQS.ReceiveMessage",
        )

    def test_kinesis_create_stream(self):
        self.do_test_requests(
            "kinesis/createstream/my-stream",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::Kinesis",
            remote_operation="CreateStream",
            remote_resource_type="AWS::Kinesis::Stream",
            remote_resource_identifier="test_stream",
            cloudformation_primary_identifier="test_stream",
            request_response_specific_attributes={
                _AWS_KINESIS_STREAM_NAME: "test_stream",
            },
            span_name="Kinesis.CreateStream",
        )

    def test_kinesis_put_record(self):
        self.do_test_requests(
            "kinesis/putrecord/my-stream",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::Kinesis",
            remote_operation="PutRecord",
            remote_resource_type="AWS::Kinesis::Stream",
            remote_resource_identifier="test_stream",
            cloudformation_primary_identifier="test_stream",
            request_response_specific_attributes={
                _AWS_KINESIS_STREAM_NAME: "test_stream",
            },
            span_name="Kinesis.PutRecord",
        )

    def test_kinesis_describe_stream(self):
        self.do_test_requests(
            "kinesis/describestream/my-stream",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::Kinesis",
            remote_operation="DescribeStream",
            remote_resource_type="AWS::Kinesis::Stream",
            remote_resource_identifier="test-stream",
            cloudformation_primary_identifier="test-stream",
            remote_resource_account_id="000000000000",
            remote_resource_region="us-east-1",
            request_response_specific_attributes={
                _AWS_KINESIS_STREAM_NAME: "test-stream",
                _AWS_KINESIS_STREAM_ARN: r"arn:aws:kinesis:us-east-1:000000000000:stream/test-stream",
            },
            span_name="Kinesis.DescribeStream",
        )

    def test_kinesis_error(self):
        self.do_test_requests(
            "kinesis/error",
            "GET",
            400,
            1,
            0,
            remote_service="AWS::Kinesis",
            remote_operation="DeleteStream",
            remote_resource_type="AWS::Kinesis::Stream",
            remote_resource_identifier="test_stream_error",
            cloudformation_primary_identifier="test_stream_error",
            request_response_specific_attributes={
                _AWS_KINESIS_STREAM_NAME: "test_stream_error",
            },
            span_name="Kinesis.DeleteStream",
        )

    # TODO: https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/83
    # def test_kinesis_fault(self):
    #     self.do_test_requests(
    #         "kinesis/fault",
    #         "GET",
    #         500,
    #         0,
    #         1,
    #         remote_service="AWS::Kinesis",
    #         remote_operation="CreateStream",
    #         remote_resource_type="AWS::Kinesis::Stream",
    #         remote_resource_identifier="test_stream",
    #         request_response_specific_attributes={
    #             _AWS_KINESIS_STREAM_NAME: "test_stream",
    #         },
    #         span_name="Kinesis.CreateStream",
    #     )

    def test_secretsmanager_create_secret(self):
        self.do_test_requests(
            "secretsmanager/createsecret/some-secret",
            "GET",
            200,
            0,
            0,
            rpc_service="Secrets Manager",
            remote_service="AWS::SecretsManager",
            remote_operation="CreateSecret",
            remote_resource_type="AWS::SecretsManager::Secret",
            remote_resource_identifier=r"test-secret-[a-zA-Z0-9]{6}$",
            cloudformation_primary_identifier=r"arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-[a-zA-Z0-9]{6}$",
            request_response_specific_attributes={
                _AWS_SECRETSMANAGER_SECRET_ARN: r"arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-[a-zA-Z0-9]{6}$",
            },
            span_name="Secrets Manager.CreateSecret",
        )

    def test_secretsmanager_get_secret_value(self):
        self.do_test_requests(
            "secretsmanager/getsecretvalue/some-secret",
            "GET",
            200,
            0,
            0,
            rpc_service="Secrets Manager",
            remote_service="AWS::SecretsManager",
            remote_operation="GetSecretValue",
            remote_resource_type="AWS::SecretsManager::Secret",
            remote_resource_identifier=r"test-secret-[a-zA-Z0-9]{6}$",
            cloudformation_primary_identifier=r"arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-[a-zA-Z0-9]{6}$",
            request_response_specific_attributes={
                _AWS_SECRETSMANAGER_SECRET_ARN: r"arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-[a-zA-Z0-9]{6}$",
            },
            span_name="Secrets Manager.GetSecretValue",
        )

    def test_secretsmanager_error(self):
        self.do_test_requests(
            "secretsmanager/error",
            "GET",
            400,
            1,
            0,
            rpc_service="Secrets Manager",
            remote_service="AWS::SecretsManager",
            remote_operation="DescribeSecret",
            remote_resource_type="AWS::SecretsManager::Secret",
            remote_resource_identifier="test-secret-error",
            cloudformation_primary_identifier="arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-error",
            request_response_specific_attributes={
                _AWS_SECRETSMANAGER_SECRET_ARN: "arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-error",
            },
            span_name="Secrets Manager.DescribeSecret",
        )

    # TODO: https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/83
    # def test_secretsmanager_fault(self):
    #     self.do_test_requests(
    #         "secretsmanager/fault",
    #         "GET",
    #         500,
    #         0,
    #         1,
    #         rpc_service="Secrets Manager",
    #         remote_service="AWS::SecretsManager",
    #         remote_operation="CreateSecret",
    #         remote_resource_type="AWS::SecretsManager::Secret",
    #         remote_resource_identifier="test-secret-error",
    #         cloudformation_primary_identifier="arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-error",
    #         request_response_specific_attributes={
    #             _AWS_SECRETSMANAGER_SECRET_ARN: "arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-error",
    #         },
    #         span_name="Secrets Manager.CreateSecret",
    #     )

    def test_sns_publish(self):
        self.do_test_requests(
            "sns/publish/some-topic",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::SNS",
            remote_operation="Publish",
            remote_resource_type="AWS::SNS::Topic",
            remote_resource_identifier="test-topic",
            cloudformation_primary_identifier="arn:aws:sns:us-east-1:000000000000:test-topic",
            request_response_specific_attributes={
                _AWS_SNS_TOPIC_ARN: "arn:aws:sns:us-east-1:000000000000:test-topic",
            },
            span_name="SNS.Publish",
        )

    def test_sns_error(self):
        self.do_test_requests(
            "sns/error",
            "GET",
            400,
            1,
            0,
            remote_service="AWS::SNS",
            remote_operation="Publish",
            remote_resource_type="AWS::SNS::Topic",
            remote_resource_identifier="test-topic-error",
            cloudformation_primary_identifier="arn:aws:sns:us-east-1:000000000000:test-topic-error",
            request_response_specific_attributes={
                _AWS_SNS_TOPIC_ARN: "arn:aws:sns:us-east-1:000000000000:test-topic-error",
            },
            span_name="SNS.Publish",
        )

    # TODO: https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/83
    # def test_sns_fault(self):
    #     self.do_test_requests(
    #         "sns/fault",
    #         "GET",
    #         500,
    #         0,
    #         1,
    #         remote_service="AWS::SNS",
    #         remote_operation="GetTopicAttributes",
    #         remote_resource_type="AWS::SNS::Topic",
    #         remote_resource_identifier="invalid-topic",
    #         cloudformation_primary_identifier="arn:aws:sns:us-east-1:000000000000:invalid-topic",
    #         request_response_specific_attributes={
    #            _AWS_SNS_TOPIC_ARN: "arn:aws:sns:us-east-1:000000000000:invalid-topic",},
    #         span_name="SNS.GetTopicAttributes"
    #     )

    def test_stepfunctions_describe_state_machine(self):
        self.do_test_requests(
            "stepfunctions/describestatemachine/some-state-machine",
            "GET",
            200,
            0,
            0,
            rpc_service="SFN",
            remote_service="AWS::StepFunctions",
            remote_operation="DescribeStateMachine",
            remote_resource_type="AWS::StepFunctions::StateMachine",
            remote_resource_identifier="test-state-machine",
            cloudformation_primary_identifier="arn:aws:states:us-east-1:000000000000:stateMachine:test-state-machine",
            request_response_specific_attributes={
                _AWS_STEPFUNCTIONS_STATE_MACHINE_ARN: "arn:aws:states:us-east-1:000000000000:stateMachine:test-state-machine",
            },
            span_name="SFN.DescribeStateMachine",
        )

    def test_stepfunctions_describe_activity(self):
        self.do_test_requests(
            "stepfunctions/describeactivity/some-activity",
            "GET",
            200,
            0,
            0,
            rpc_service="SFN",
            remote_service="AWS::StepFunctions",
            remote_operation="DescribeActivity",
            remote_resource_type="AWS::StepFunctions::Activity",
            remote_resource_identifier="test-activity",
            cloudformation_primary_identifier="arn:aws:states:us-east-1:000000000000:activity:test-activity",
            request_response_specific_attributes={
                _AWS_STEPFUNCTIONS_ACTIVITY_ARN: "arn:aws:states:us-east-1:000000000000:activity:test-activity",
            },
            span_name="SFN.DescribeActivity",
        )

    def test_stepfunctions_error(self):
        self.do_test_requests(
            "stepfunctions/error",
            "GET",
            400,
            1,
            0,
            rpc_service="SFN",
            remote_service="AWS::StepFunctions",
            remote_operation="DescribeStateMachine",
            remote_resource_type="AWS::StepFunctions::StateMachine",
            remote_resource_identifier="error-state-machine",
            cloudformation_primary_identifier="arn:aws:states:us-east-1:000000000000:stateMachine:error-state-machine",
            request_response_specific_attributes={
                _AWS_STEPFUNCTIONS_STATE_MACHINE_ARN: "arn:aws:states:us-east-1:000000000000:stateMachine:error-state-machine",
            },
            span_name="SFN.DescribeStateMachine",
        )


    # TODO: https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/83
    # def test_stepfunctions_fault(self):
    #     self.do_test_requests(
    #         "stepfunctions/fault",
    #         "GET",
    #         500,
    #         0,
    #         1,
    #         rpc_service="SFN",
    #         remote_service="AWS::StepFunctions",
    #         remote_operation="ListStateMachineVersions",
    #         remote_resource_type="AWS::StepFunctions::StateMachine",
    #         remote_resource_identifier="invalid-state-machine",
    #         cloudformation_primary_identifier="arn:aws:states:us-east-1:000000000000:stateMachine:invalid-state-machine",
    #         request_response_specific_attributes={
    #            _AWS_STEPFUNCTIONS_STATE_MACHINE_ARN: "arn:aws:states:us-east-1:000000000000:stateMachine:invalid-state-machine",},
    #         span_name="SFN.ListStateMachineVersions",
    #     )

    def test_bedrock_get_guardrail(self):
        self.do_test_requests(
            "bedrock/getguardrail/get-guardrail",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock",
            remote_service="AWS::Bedrock",
            remote_operation="GetGuardrail",
            remote_resource_type="AWS::Bedrock::Guardrail",
            remote_resource_identifier="test-guardrail",
            cloudformation_primary_identifier="arn:aws:bedrock:us-west-2:12345678901:guardrail/test-guardrail",
            request_response_specific_attributes={
                _AWS_BEDROCK_GUARDRAIL_ID: "test-guardrail",
                _AWS_BEDROCK_GUARDRAIL_ARN: "arn:aws:bedrock:us-west-2:12345678901:guardrail/test-guardrail",
            },
            span_name="Bedrock.GetGuardrail",
        )

    def test_bedrock_runtime_invoke_model_nova(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-nova",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="us.amazon.nova-micro-v1:0",
            cloudformation_primary_identifier="us.amazon.nova-micro-v1:0",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "us.amazon.nova-micro-v1:0",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 456,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 789,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_titan(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-titan",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="amazon.titan-text-express-v1",
            cloudformation_primary_identifier="amazon.titan-text-express-v1",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "amazon.titan-text-express-v1",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 456,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 789,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_claude(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-claude",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="us.anthropic.claude-3-5-haiku-20241022-v1:0",
            cloudformation_primary_identifier="us.anthropic.claude-3-5-haiku-20241022-v1:0",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "us.anthropic.claude-3-5-haiku-20241022-v1:0",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 456,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 789,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_llama(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-llama",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="meta.llama3-8b-instruct-v1:0",
            cloudformation_primary_identifier="meta.llama3-8b-instruct-v1:0",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "meta.llama3-8b-instruct-v1:0",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 456,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 789,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_command(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-command",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="cohere.command-r-v1:0",
            cloudformation_primary_identifier="cohere.command-r-v1:0",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "cohere.command-r-v1:0",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 12,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 10,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_jamba(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-jamba",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="ai21.jamba-1-5-large-v1:0",
            cloudformation_primary_identifier="ai21.jamba-1-5-large-v1:0",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "ai21.jamba-1-5-large-v1:0",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 456,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 789,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_runtime_invoke_model_mistral(self):
        self.do_test_requests(
            "bedrock/invokemodel/invoke-model-mistral",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Runtime",
            remote_service="AWS::BedrockRuntime",
            remote_operation="InvokeModel",
            remote_resource_type="AWS::Bedrock::Model",
            remote_resource_identifier="mistral.mistral-7b-instruct-v0:2",
            cloudformation_primary_identifier="mistral.mistral-7b-instruct-v0:2",
            request_response_specific_attributes={
                _GEN_AI_SYSTEM: "aws.bedrock",
                _GEN_AI_REQUEST_MODEL: "mistral.mistral-7b-instruct-v0:2",
                _GEN_AI_REQUEST_TEMPERATURE: 0.123,
                _GEN_AI_REQUEST_TOP_P: 0.456,
                _GEN_AI_REQUEST_MAX_TOKENS: 123,
                _GEN_AI_USAGE_INPUT_TOKENS: 12,
                _GEN_AI_USAGE_OUTPUT_TOKENS: 10,
                _GEN_AI_RESPONSE_FINISH_REASONS: ["finish_reason"],
            },
            span_name="Bedrock Runtime.InvokeModel",
        )

    def test_bedrock_agent_runtime_invoke_agent(self):
        self.do_test_requests(
            "bedrock/invokeagent/invoke-agent",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Agent Runtime",
            remote_service="AWS::Bedrock",
            remote_operation="InvokeAgent",
            remote_resource_type="AWS::Bedrock::Agent",
            remote_resource_identifier="test-agent",
            cloudformation_primary_identifier="test-agent",
            request_response_specific_attributes={
                _AWS_BEDROCK_AGENT_ID: "test-agent",
            },
            span_name="Bedrock Agent Runtime.InvokeAgent",
        )

    def test_bedrock_agent_runtime_retrieve(self):
        self.do_test_requests(
            "bedrock/retrieve/retrieve",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Agent Runtime",
            remote_service="AWS::Bedrock",
            remote_operation="Retrieve",
            remote_resource_type="AWS::Bedrock::KnowledgeBase",
            remote_resource_identifier="test-knowledge-base",
            cloudformation_primary_identifier="test-knowledge-base",
            request_response_specific_attributes={
                _AWS_BEDROCK_KNOWLEDGE_BASE_ID: "test-knowledge-base",
            },
            span_name="Bedrock Agent Runtime.Retrieve",
        )

    def test_bedrock_agent_get_agent(self):
        self.do_test_requests(
            "bedrock/getagent/get-agent",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Agent",
            remote_service="AWS::Bedrock",
            remote_operation="GetAgent",
            remote_resource_type="AWS::Bedrock::Agent",
            remote_resource_identifier="test-agent",
            cloudformation_primary_identifier="test-agent",
            request_response_specific_attributes={
                _AWS_BEDROCK_AGENT_ID: "test-agent",
            },
            span_name="Bedrock Agent.GetAgent",
        )

    def test_bedrock_agent_get_knowledge_base(self):
        self.do_test_requests(
            "bedrock/getknowledgebase/get-knowledge-base",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Agent",
            remote_service="AWS::Bedrock",
            remote_operation="GetKnowledgeBase",
            remote_resource_type="AWS::Bedrock::KnowledgeBase",
            remote_resource_identifier="test-knowledge-base",
            cloudformation_primary_identifier="test-knowledge-base",
            request_response_specific_attributes={
                _AWS_BEDROCK_KNOWLEDGE_BASE_ID: "test-knowledge-base",
            },
            span_name="Bedrock Agent.GetKnowledgeBase",
        )

    def test_bedrock_agent_get_data_source(self):
        self.do_test_requests(
            "bedrock/getdatasource/get-data-source",
            "GET",
            200,
            0,
            0,
            rpc_service="Bedrock Agent",
            remote_service="AWS::Bedrock",
            remote_operation="GetDataSource",
            remote_resource_type="AWS::Bedrock::DataSource",
            remote_resource_identifier="test-data-source",
            cloudformation_primary_identifier="test-knowledge-base|test-data-source",
            request_response_specific_attributes={
                _AWS_BEDROCK_DATA_SOURCE_ID: "test-data-source",
                _AWS_BEDROCK_KNOWLEDGE_BASE_ID: "test-knowledge-base"
            },
            span_name="Bedrock Agent.GetDataSource",
        )

    def test_cross_account(self):
        self.do_test_requests(
            "cross-account/createbucket/account_b",
            "GET",
            200,
            0,
            0,
            remote_service="AWS::S3",
            remote_operation="PutBucket",
            remote_resource_type="AWS::S3::Bucket",
            remote_resource_identifier="cross-account-bucket",
            cloudformation_primary_identifier="cross-account-bucket",
            request_specific_attributes={
                SpanAttributes.AWS_S3_BUCKET: "cross-account-bucket",
            },
            remote_resource_access_key="account_b_access_key_id",
            remote_resource_region="eu-central-1",
            span_name="S3.PutBucket",
        )

    # TODO: add contract test for Lambda event source mapping resource

    @override
    def _assert_aws_span_attributes(self, resource_scope_spans: List[ResourceScopeSpan], path: str, **kwargs) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_CLIENT:
                target_spans.append(resource_scope_span.span)

        self.assertEqual(len(target_spans), 1)
        self._assert_aws_attributes(
            target_spans[0].attributes,
            kwargs.get("remote_service"),
            kwargs.get("remote_operation"),
            "CLIENT",
            kwargs.get("remote_resource_type", "None"),
            kwargs.get("remote_resource_identifier", "None"),
            kwargs.get("cloudformation_primary_identifier", "None"),
            kwargs.get("remote_resource_account_id", "None"),
            kwargs.get("remote_resource_access_key", "None"),
            kwargs.get("remote_resource_region", "None")
        )

    def _assert_aws_attributes(
        self,
        attributes_list: List[KeyValue],
        service: str,
        operation: str,
        span_kind: str,
        remote_resource_type: str,
        remote_resource_identifier: str,
        cloudformation_primary_identifier: str,
        remote_resource_account_id: str,
        remote_resource_access_key: str,
        remote_resource_region: str,      
    ) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self._assert_str_attribute(attributes_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_SERVICE, service)
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_OPERATION, operation)
        if remote_resource_type != "None":
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_TYPE, remote_resource_type)
        if remote_resource_identifier != "None":
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_IDENTIFIER, remote_resource_identifier)
        if cloudformation_primary_identifier != "None":
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_CLOUDFORMATION_PRIMARY_IDENTIFIER, cloudformation_primary_identifier)
        if remote_resource_account_id != "None":
            assert remote_resource_identifier != "None"
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_ACCOUNT_ID, remote_resource_account_id)
            self.assertIsNone(attributes_dict.get(AWS_REMOTE_RESOURCE_ACCESS_KEY))
        if remote_resource_access_key != "None":
            assert remote_resource_identifier != "None"
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_ACCESS_KEY, remote_resource_access_key)
            self.assertIsNone(attributes_dict.get(AWS_REMOTE_RESOURCE_ACCOUNT_ID))
        if remote_resource_region != "None":
            self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_REGION, remote_resource_region)
            self._assert_str_attribute(attributes_dict, AWS_SPAN_KIND, span_kind)

    @override
    def _assert_semantic_conventions_span_attributes(
        self, resource_scope_spans: List[ResourceScopeSpan], method: str, path: str, status_code: int, **kwargs
    ) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_CLIENT:
                target_spans.append(resource_scope_span.span)

        self.assertEqual(len(target_spans), 1)
        self.assertEqual(target_spans[0].name, kwargs.get("span_name"))
        self._assert_semantic_conventions_attributes(
            target_spans[0].attributes,
            # For most cases, rpc_service is the same as the service name after "AWS::" prefix. Bedrock services are
            # the only exception to this, so we pass the rpc_service explicitly in the test case.
            kwargs.get("rpc_service") if "rpc_service" in kwargs else kwargs.get("remote_service").split("::")[-1],
            kwargs.get("remote_service"),
            kwargs.get("remote_operation"),
            status_code,
            kwargs.get("request_response_specific_attributes", {}),
        )

    # pylint: disable=unidiomatic-typecheck
    def _assert_semantic_conventions_attributes(
        self,
        attributes_list: List[KeyValue],
        rpc_service: str,
        service: str,
        operation: str,
        status_code: int,
        request_response_specific_attributes: dict,
    ) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self._assert_str_attribute(attributes_dict, SpanAttributes.RPC_METHOD, operation)
        self._assert_str_attribute(attributes_dict, SpanAttributes.RPC_SYSTEM, "aws-api")
        self._assert_str_attribute(attributes_dict, SpanAttributes.RPC_SERVICE, rpc_service)
        self._assert_int_attribute(attributes_dict, SpanAttributes.HTTP_STATUS_CODE, status_code)
        for key, value in request_response_specific_attributes.items():
            if isinstance(value, str):
                self._assert_str_attribute(attributes_dict, key, value)
            elif isinstance(value, int):
                self._assert_int_attribute(attributes_dict, key, value)
            elif isinstance(value, float):
                self._assert_float_attribute(attributes_dict, key, value)
            # value is a list: gen_ai.response.finish_reasons or aws.table_name
            elif key == _GEN_AI_RESPONSE_FINISH_REASONS:
                self._assert_invoke_model_finish_reasons(attributes_dict, key, value)
            else:
                self._assert_array_value_ddb_table_name(attributes_dict, key, value)

    @override
    def _assert_metric_attributes(
        self,
        resource_scope_metrics: List[ResourceScopeMetric],
        metric_name: str,
        expected_sum: int,
        **kwargs,
    ) -> None:
        target_metrics: List[Metric] = []
        for resource_scope_metric in resource_scope_metrics:
            if resource_scope_metric.metric.name.lower() == metric_name.lower():
                target_metrics.append(resource_scope_metric.metric)
        # For bedrock test cases, extra metric is generated from internally generated response. remove it here
        self._filter_bedrock_metrics(target_metrics)
        if (len(target_metrics) == 2):
            dependency_target_metric: Metric = target_metrics[0]
            service_target_metric: Metric = target_metrics[1]
            # Test dependency metric
            dep_dp_list: List[ExponentialHistogramDataPoint] = dependency_target_metric.exponential_histogram.data_points
            dep_dp_list_count: int = kwargs.get("dp_count", 1)
            self.assertEqual(len(dep_dp_list), dep_dp_list_count)
            dependency_dp: ExponentialHistogramDataPoint = dep_dp_list[0]
            service_dp_list = service_target_metric.exponential_histogram.data_points
            service_dp_list_count = kwargs.get("dp_count", 1)
            self.assertEqual(len(service_dp_list), service_dp_list_count)
            service_dp: ExponentialHistogramDataPoint = service_dp_list[0]
            if len(service_dp_list[0].attributes) > len(dep_dp_list[0].attributes):
                dependency_dp = service_dp_list[0]
                service_dp = dep_dp_list[0]
            self._assert_dependency_dp_attributes(dependency_dp, expected_sum, metric_name, **kwargs)
            self._assert_service_dp_attributes(service_dp, expected_sum, metric_name)
        elif (len(target_metrics) == 1):
            target_metric: Metric = target_metrics[0]
            dp_list: List[ExponentialHistogramDataPoint] = target_metric.exponential_histogram.data_points
            dp_list_count: int = kwargs.get("dp_count", 2)
            self.assertEqual(len(dp_list), dp_list_count)
            dependency_dp: ExponentialHistogramDataPoint = dp_list[0]
            service_dp: ExponentialHistogramDataPoint = dp_list[1]
            if len(dp_list[1].attributes) > len(dp_list[0].attributes):
                dependency_dp = dp_list[1]
                service_dp = dp_list[0]
            self._assert_dependency_dp_attributes(dependency_dp, expected_sum, metric_name, **kwargs)
            self._assert_service_dp_attributes(service_dp, expected_sum, metric_name)
        else:
            raise AssertionError("Target metrics count is incorrect")
    
    def _assert_dependency_dp_attributes(self, dependency_dp: ExponentialHistogramDataPoint, expected_sum: int, metric_name: str, **kwargs):
        attribute_dict = self._get_attributes_dict(dependency_dp.attributes)
        self._assert_str_attribute(attribute_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attribute_dict, AWS_REMOTE_SERVICE, kwargs.get("remote_service"))
        self._assert_str_attribute(attribute_dict, AWS_REMOTE_OPERATION, kwargs.get("remote_operation"))
        self._assert_str_attribute(attribute_dict, AWS_SPAN_KIND, "CLIENT")
        
        remote_resource_type = kwargs.get("remote_resource_type", "None")
        remote_resource_identifier = kwargs.get("remote_resource_identifier", "None")
        remote_resource_account_id = kwargs.get("remote_resource_account_id", "None")
        remote_resource_access_key = kwargs.get("remote_resource_access_key", "None")
        remote_resource_region = kwargs.get("remote_resource_region", "None")
        if remote_resource_type != "None":
            self._assert_str_attribute(attribute_dict, AWS_REMOTE_RESOURCE_TYPE, remote_resource_type)
        if remote_resource_identifier != "None":
            self._assert_str_attribute(attribute_dict, AWS_REMOTE_RESOURCE_IDENTIFIER, remote_resource_identifier)
        if remote_resource_account_id != "None":
            assert remote_resource_identifier != "None"
            self._assert_str_attribute(attribute_dict, AWS_REMOTE_RESOURCE_ACCOUNT_ID, remote_resource_account_id)
            self.assertIsNone(attribute_dict.get(AWS_REMOTE_RESOURCE_ACCESS_KEY))
        if remote_resource_access_key != "None":
            assert remote_resource_identifier != "None"
            self._assert_str_attribute(attribute_dict, AWS_REMOTE_RESOURCE_ACCESS_KEY, remote_resource_access_key)
            self.assertIsNone(attribute_dict.get(AWS_REMOTE_RESOURCE_ACCOUNT_ID))
        if remote_resource_region != "None":
            self._assert_str_attribute(attribute_dict, AWS_REMOTE_RESOURCE_REGION, remote_resource_region)
        
        self.check_sum(metric_name, dependency_dp.sum, expected_sum)

    def _assert_service_dp_attributes(self, service_dp: ExponentialHistogramDataPoint, expected_sum: int, metric_name: str):
        attribute_dict = self._get_attributes_dict(service_dp.attributes)
        self._assert_str_attribute(attribute_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attribute_dict, AWS_SPAN_KIND, "LOCAL_ROOT")
        self.check_sum(metric_name, service_dp.sum, expected_sum)

    # pylint: disable=consider-using-enumerate
    def _assert_array_value_ddb_table_name(self, attributes_dict: Dict[str, AnyValue], key: str, expect_values: list):
        self.assertIn(key, attributes_dict)
        self.assertEqual(attributes_dict[key].string_value, expect_values[0])
    
    def _assert_invoke_model_finish_reasons(self, attributes_dict: Dict[str, AnyValue], key: str, expect_values: list):
        self.assertIn(key, attributes_dict)
        self.assertEqual(len(attributes_dict[key].array_value.values), len(expect_values))
        for i, value in enumerate(expect_values):
            self.assertEqual(attributes_dict[key].array_value.values[i].string_value, value)

    def _filter_bedrock_metrics(self, target_metrics: List[Metric]):
        bedrock_calls = {
            "GET agents/test-agent",
            "GET guardrails/test-guardrail",
            "GET knowledgebases/test-knowledge-base",
            "GET knowledgebases/test-knowledge-base/datasources/test-data-source",
            "POST agents/test-agent/agentAliases/test-agent-alias/sessions/test-session/text",
            "POST model/us.amazon.nova-micro-v1:0/invoke",
            "POST model/amazon.titan-text-express-v1/invoke",
            "POST model/us.anthropic.claude-3-5-haiku-20241022-v1:0/invoke",
            "POST model/meta.llama3-8b-instruct-v1:0/invoke",
            "POST model/cohere.command-r-v1:0/invoke",
            "POST model/ai21.jamba-1-5-large-v1:0/invoke",
            "POST model/mistral.mistral-7b-instruct-v0:2/invoke",
            "POST knowledgebases/test-knowledge-base/retrieve"
        }
        for metric in target_metrics:
            for dp in metric.exponential_histogram.data_points:
                # remove dp generated from manual response
                attribute_dict = self._get_attributes_dict(dp.attributes)
                if attribute_dict['aws.local.operation'].string_value in bedrock_calls:
                    metric.exponential_histogram.data_points.remove(dp)
            # remove Metric if it has no data points
            if (len(metric.exponential_histogram.data_points) == 0):
                target_metrics.remove(metric)

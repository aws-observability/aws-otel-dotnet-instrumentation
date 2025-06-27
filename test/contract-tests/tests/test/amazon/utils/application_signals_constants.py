# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""
Constants for attributes and metric names defined in Application Signals.
"""

# Metric names
LATENCY_METRIC: str = "latency"
ERROR_METRIC: str = "error"
FAULT_METRIC: str = "fault"
GC_COUNT_METRIC: str = "process.runtime.dotnet.gc.collections.count"
GC_DURATION_METRIC: str = "process.runtime.dotnet.gc.duration"
GC_HEAP_METRIC: str = "process.runtime.dotnet.gc.heap.size"
THREAD_COUNT_METRIC: str = "process.runtime.dotnet.thread_pool.threads.count"
THREAD_QUEUE_METRIC: str = "process.runtime.dotnet.thread_pool.queue.length"

# Attribute names
AWS_LOCAL_SERVICE: str = "aws.local.service"
AWS_LOCAL_OPERATION: str = "aws.local.operation"
AWS_REMOTE_SERVICE: str = "aws.remote.service"
AWS_REMOTE_OPERATION: str = "aws.remote.operation"
AWS_REMOTE_RESOURCE_TYPE: str = "aws.remote.resource.type"
AWS_REMOTE_RESOURCE_IDENTIFIER: str = "aws.remote.resource.identifier"
AWS_REMOTE_CLOUDFORMATION_PRIMARY_IDENTIFIER: str = "aws.remote.resource.cfn.primary.identifier"
AWS_SPAN_KIND: str = "aws.span.kind"
HTTP_RESPONSE_STATUS: str = "http.response.status_code"
HTTP_REQUEST_METHOD: str = "http.request.method"
AWS_REMOTE_RESOURCE_ACCESS_KEY: str = "aws.remote.resource.account.access_key"
AWS_REMOTE_RESOURCE_ACCOUNT_ID: str = "aws.remote.resource.account.id"
AWS_REMOTE_RESOURCE_REGION: str = "aws.remote.resource.region"
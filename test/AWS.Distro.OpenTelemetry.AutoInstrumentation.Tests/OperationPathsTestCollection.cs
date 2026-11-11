// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Tests that read/write the process-global OTEL_AWS_HTTP_OPERATION_PATHS environment variable and
// the cached operation paths must not run concurrently, since xUnit runs test classes in parallel
// by default. Classes tagged with this collection are serialized relative to one another.
[CollectionDefinition("OperationPaths")]
public class OperationPathsTestCollection
{
}

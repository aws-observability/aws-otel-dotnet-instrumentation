// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Tests that set and clear process-global OTEL_* environment variables must not run concurrently,
// since xUnit runs test classes in parallel by default and one class clearing a variable would
// otherwise change what another observes. Classes tagged with this collection are serialized
// relative to one another.
[CollectionDefinition("EnvironmentVariables")]
public class EnvironmentVariablesTestCollection
{
}

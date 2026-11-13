// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Logging;

// ConsoleLogger writes to the process-global Console.Out and ConsoleLoggerProvider reads the process-global
// APPLICATION_SIGNALS_LOG_LEVEL environment variable. Both are swapped out by these tests, so they must not
// run concurrently with any other test that writes to the console or reads that variable.
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleLoggerTestCollection
{
    public const string Name = "ConsoleLogger";
}

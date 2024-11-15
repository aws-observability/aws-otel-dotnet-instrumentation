// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;

/// <summary>
/// ConsoleLoggerProvider class
/// </summary>
public class ConsoleLoggerProvider : ILoggerProvider
{
    private readonly LogLevel minLogLevel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLoggerProvider"/> class.
    /// </summary>
    public ConsoleLoggerProvider()
    {
        // Read the log level from the environment variable
        string logLevelEnv = Environment.GetEnvironmentVariable("APPLICATION_SIGNALS_LOG_LEVEL") ?? "Information";

        if (!Enum.TryParse<LogLevel>(logLevelEnv, true, out this.minLogLevel))
        {
            this.minLogLevel = LogLevel.Information;
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        return new ConsoleLogger(categoryName, this.minLogLevel);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Dispose resources if necessary
    }
}

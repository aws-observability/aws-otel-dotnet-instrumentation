// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;

/// <summary>
/// ConsoleLogger class
/// </summary>
public class ConsoleLogger : ILogger
{
    private readonly string categoryName;
    private readonly LogLevel minLogLevel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLogger"/> class.
    /// </summary>
    /// <param name="categoryName">The class that is writing the log</param>
    /// <param name="minLogLevel">The log level from the log statement</param>
    public ConsoleLogger(string categoryName, LogLevel minLogLevel)
    {
        this.categoryName = categoryName;
        this.minLogLevel = minLogLevel;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        // Scopes are not implemented in this simple logger
        return NullScope.Instance;
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= this.minLogLevel;
    }

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        if (formatter == null)
        {
            throw new ArgumentNullException(nameof(formatter));
        }

        string message = formatter(state, exception);

        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        string logLevelString = this.GetLogLevelString(logLevel);

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevelString}] {this.categoryName}: {message}");

        if (exception != null)
        {
            Console.WriteLine(exception.ToString());
        }
    }

    private string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => "Unknown",
        };
    }

    private class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();

        public void Dispose()
        {
        }
    }
}

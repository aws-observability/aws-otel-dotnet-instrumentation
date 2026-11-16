// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Lambda detection is read once into a static field, so each case loads its own copy of the plugin assembly after
// setting AWS_LAMBDA_FUNCTION_NAME. The collection doesn't run in parallel, because this changes environment
// variables and Console.Out.
[Collection("AWS credential providers")]
public class PluginConsoleLogsTests
{
    private const int LogCount = 3;
    private const string Message = "console-logs-test";

    [Theory]
    [InlineData(true, "console", (ConsoleExporterOutputTargets)0, LogCount, 0)]
    [InlineData(true, "console,otlp", (ConsoleExporterOutputTargets)0, LogCount, 0)]
    [InlineData(false, "console", ConsoleExporterOutputTargets.Console, 0, LogCount)]
    [InlineData(false, "console,otlp", ConsoleExporterOutputTargets.Console, 0, LogCount)]
    [InlineData(true, "otlp", null, 0, 0)]
    [InlineData(true, "none", null, 0, 0)]
    [InlineData(true, null, null, 0, 0)]
    public void ShouldPrintEachLogAtMostOnceOnStdout(
        bool isLambda, string? exporters, ConsoleExporterOutputTargets? expectedTargets, int expectedCompact, int expectedUpstream)
    {
        var originalOut = Console.Out;
        var originalFunctionName = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
        var originalExporters = Environment.GetEnvironmentVariable("OTEL_LOGS_EXPORTER");
        var context = new AssemblyLoadContext(nameof(PluginConsoleLogsTests), isCollectible: true);
        var output = new StringWriter();
        ConsoleExporterOutputTargets? targets = null;
        try
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", isLambda ? "test-function" : null);
            Environment.SetEnvironmentVariable("OTEL_LOGS_EXPORTER", exporters);
            Console.SetOut(output);

            var pluginType = context.LoadFromAssemblyPath(typeof(Plugin).Assembly.Location).GetType(typeof(Plugin).FullName!)!;
            var plugin = Activator.CreateInstance(pluginType);
            var configureLogs = pluginType.GetMethod(nameof(Plugin.ConfigureLogsOptions), new[] { typeof(OpenTelemetryLoggerOptions) })!;
            var configureConsole = pluginType.GetMethod(nameof(Plugin.ConfigureLogsOptions), new[] { typeof(ConsoleExporterOptions) })!;

            // Same order as upstream LoggerInitializer: the logger options hook, then the console exporter if selected.
            using (var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            {
                configureLogs.Invoke(plugin, new object[] { options });
                if (exporters?.Split(',').Contains("console") == true)
                {
                    options.AddConsoleExporter(consoleOptions =>
                    {
                        configureConsole.Invoke(plugin, new object[] { consoleOptions });
                        targets = consoleOptions.Targets;
                    });
                }
            })))
            {
                var logger = loggerFactory.CreateLogger("test");
                for (var i = 0; i < LogCount; i++)
                {
                    logger.LogInformation(Message);
                }
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", originalFunctionName);
            Environment.SetEnvironmentVariable("OTEL_LOGS_EXPORTER", originalExporters);
            context.Unload();
        }

        var lines = output.ToString().Split('\n');
        Assert.Equal(expectedTargets, targets);
        Assert.Equal(expectedCompact, lines.Count(line => line.StartsWith('{') && line.Contains($"\"body\":\"{Message}\"")));
        Assert.Equal(expectedUpstream, lines.Count(line => line.StartsWith("LogRecord.Timestamp:")));
    }
}

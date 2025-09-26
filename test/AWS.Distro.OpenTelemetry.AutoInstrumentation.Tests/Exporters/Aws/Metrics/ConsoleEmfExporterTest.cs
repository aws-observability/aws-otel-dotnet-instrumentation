// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics;
using FluentAssertions;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ConsoleEmfExporterTest
{
    [Fact]
    public void TestNamespaceInitialization()
    {
        // Test default namespace
        var defaultExporter = new ConsoleEmfExporter();
        var namespaceField = typeof(EmfExporterBase).GetField("_namespace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        namespaceField?.GetValue(defaultExporter).Should().Be("default");

        // Test custom namespace
        var customExporter = new ConsoleEmfExporter("CustomNamespace");
        namespaceField?.GetValue(customExporter).Should().Be("CustomNamespace");

        // Test null namespace (passes null as-is since constructor doesn't handle null)
        var nullNamespaceExporter = new ConsoleEmfExporter(null!);
        namespaceField?.GetValue(nullNamespaceExporter).Should().BeNull();
    }

    [Fact]
    public async Task TestSendLogEvent()
    {
        var exporter = new ConsoleEmfExporter();

        // Create a test EMF log structure
        var testMessage = new EmfLog
        {
            ["_aws"] = new AwsMetadata
            {
                Timestamp = 1640995200000,
                CloudWatchMetrics = new[]
                {
                    new CloudWatchMetric
                    {
                        Namespace = "TestNamespace",
                        Dimensions = new[] { new[] { "Service" } },
                        Metrics = new[]
                        {
                            new MetricDefinition
                            {
                                Name = "TestMetric",
                                Unit = "Count"
                            }
                        }
                    }
                }
            },
            ["Service"] = "test-service",
            ["TestMetric"] = 42
        };

        var logEvent = new LogEvent
        {
            Message = JsonSerializer.Serialize(testMessage),
            Timestamp = 1640995200000
        };

        // Capture console output
        var originalOut = Console.Out;
        using var stringWriter = new StringWriter();
        Console.SetOut(stringWriter);

        try
        {
            // Call the method using reflection to access protected method
            var sendLogEventMethod = typeof(ConsoleEmfExporter).GetMethod("SendLogEventAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)sendLogEventMethod?.Invoke(exporter, new object[] { logEvent })!;

            // Verify the message was printed to console
            var output = stringWriter.ToString().Trim();
            output.Should().Be(logEvent.Message);

            // Verify the content of the logged message
            var loggedMessage = JsonSerializer.Deserialize<EmfLog>(output);
            loggedMessage.Should().NotBeNull();
            loggedMessage!.Should().ContainKey("Service");
            loggedMessage.Should().ContainKey("TestMetric");
            loggedMessage.Should().ContainKey("_aws");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task TestForceFlushAsync()
    {
        var exporter = new ConsoleEmfExporter();
        
        // Should complete without throwing
        await exporter.ForceFlushAsync(default);
    }

    [Fact]
    public async Task TestShutdownAsync()
    {
        var exporter = new ConsoleEmfExporter();
        
        // Should complete without throwing
        await exporter.ShutdownAsync(default);
    }
}
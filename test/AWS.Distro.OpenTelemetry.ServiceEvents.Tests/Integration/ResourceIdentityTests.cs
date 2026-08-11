// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// Pins the resource attributes that every ServiceEvents signal must carry regardless of
/// infrastructure detection.
/// </summary>
/// <remarks>
/// <c>RESOURCE_DETECTORS_ENABLED=false</c> exists to skip AWS infra/IMDS lookups (EC2/EKS/ECS
/// metadata calls that are slow or absent off-AWS). It used to also skip <c>AddTelemetrySdk()</c>,
/// because that call sat inside the same detector builder — so turning off infra detection silently
/// stripped <c>telemetry.sdk.*</c> off every record. <c>process.pid</c> was never set at all. These
/// tests assert on the real emitted record rather than on internal state, so they also cover the
/// wiring between the resource builder and the exporter.
/// </remarks>
[Collection("EnvironmentVariables")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ResourceIdentityTests
{
    [Fact]
    public void WithDetectorsDisabled_EmittedRecordsStillCarrySdkIdentityAndProcessPid()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-resource-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "resource-identity-test",

                // The switch under test: infra detection off must not cost us SDK identity.
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var instrumentation = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());

                instrumentation.Initialize();
                instrumentation.IsInitialized.Should().BeTrue("enabled with OUTPUT_FILE set");

                // Flushes the startup DeploymentEvent through the real file exporter.
                ServiceEventsInstrumentation.ResetForTests();
            }

            var resource = ReadFirstRecordResource(outputFile);

            resource.TryGetProperty("telemetry.sdk.name", out var sdkName).Should().BeTrue(
                "telemetry.sdk.* is SDK identity, not infrastructure detection, so " +
                "RESOURCE_DETECTORS_ENABLED=false must not remove it");
            sdkName.GetString().Should().Be("opentelemetry");

            resource.TryGetProperty("telemetry.sdk.language", out var sdkLanguage).Should().BeTrue();
            sdkLanguage.GetString().Should().Be("dotnet");

            resource.TryGetProperty("telemetry.sdk.version", out _).Should().BeTrue();

            resource.TryGetProperty("process.pid", out var pid).Should().BeTrue(
                "process.pid identifies the emitting replica and was previously never set");
            pid.GetInt32().Should().Be(System.Environment.ProcessId);
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    /// <summary>Read the <c>resource</c> object off the first emitted NDJSON record.</summary>
    private static JsonElement ReadFirstRecordResource(string outputFile)
    {
        File.Exists(outputFile).Should().BeTrue("the file exporter should have written records");

        var firstLine = File.ReadAllLines(outputFile)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        firstLine.Should().NotBeNull("at least the startup DeploymentEvent should have been written");

        var root = JsonDocument.Parse(firstLine!).RootElement;

        root.TryGetProperty("resource", out var resource).Should().BeTrue(
            "the exporter emits resource attributes under a 'resource' object");

        return resource;
    }
}

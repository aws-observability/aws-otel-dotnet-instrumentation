// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// End-to-end smoke tests for the M1/M2 foundation: config → pipeline →
/// DeploymentEvent → OUTPUT_FILE. Drives <see cref="ServiceEventsInstrumentation" />
/// the same way the plugin hook does (build config, Initialize, Dispose) and
/// asserts the DeploymentEvent NDJSON lands in the output file.
/// </summary>
/// <remarks>
/// These tests use the real OTLP file-export pipeline (no mocks) — they are the
/// runtime proof that the foundation works before the M3–M5 collectors are built.
/// </remarks>
[Collection("EnvironmentVariables")]
public class FoundationSmokeTests
{
    [Fact]
    public void Foundation_WhenEnabledWithOutputFile_WritesDeploymentEventAtStartup()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-smoke-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var envScope = EnvScope.Isolate(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "smoke-test-service",
                ["OTEL_AWS_SERVICE_EVENTS_GIT_COMMIT_SHA"] = "abc123sha",
                ["OTEL_AWS_SERVICE_EVENTS_GIT_REPO_URL"] = "https://github.com/example/smoke",
                ["OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_ID"] = "deploy-smoke-1",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var instrumentation = ServiceEventsInstrumentation.GetOrCreate(config);

                instrumentation.Initialize();
                instrumentation.IsInitialized.Should().BeTrue("the SDK is enabled and OUTPUT_FILE is set");

                // Dispose flushes pending records and emits the shutdown DeploymentEvent.
                ServiceEventsInstrumentation.ResetForTests();
            }

            File.Exists(outputFile).Should().BeTrue("the file exporter should have created the output file");

            var lines = File.ReadAllLines(outputFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            lines.Should().NotBeEmpty("at least the startup DeploymentEvent should have been written");

            var deploymentLines = lines
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(el => el.TryGetProperty("eventName", out var name) &&
                             name.GetString() == "aws.service_events.deployment_event")
                .ToList();

            deploymentLines.Should().NotBeEmpty("a deployment_event LogRecord must be present");

            // The startup record carries trigger=startup and the deployment attributes.
            var startup = deploymentLines.FirstOrDefault(el =>
                el.GetProperty("attributes").TryGetProperty("aws.service_events.deployment.trigger", out var t) &&
                t.GetString() == "startup");

            startup.ValueKind.Should().NotBe(JsonValueKind.Undefined, "the startup DeploymentEvent should be present");

            var attrs = startup.GetProperty("attributes");
            attrs.GetProperty("event.name").GetString().Should().Be("aws.service_events.deployment_event");
            attrs.GetProperty("vcs.ref.head.revision").GetString().Should().Be("abc123sha");
            attrs.GetProperty("aws.service_events.deployment.id").GetString().Should().Be("deploy-smoke-1");
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

    [Fact]
    public void Foundation_WhenDisabled_DoesNotInitialize()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-smoke-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var envScope = EnvScope.Isolate(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "false",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "smoke-test-service",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var instrumentation = ServiceEventsInstrumentation.GetOrCreate(config);

                instrumentation.Initialize();

                instrumentation.IsInitialized.Should().BeFalse("the master kill switch is off");
            }

            File.Exists(outputFile).Should().BeFalse("nothing should be written when disabled");
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
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// Asserts the CloudWatch routing headers actually reach the wire on OTLP log export.
/// </summary>
/// <remarks>
/// <c>LogGroup</c> and <c>LogStream</c> were parsed from the environment but never used: no exporter
/// set <c>x-aws-log-group</c> or <c>x-aws-log-stream</c>, and <c>LogStream</c> had no fallback, so
/// records reached a collector with no routing metadata while Java
/// (<c>TelemendInstrumentation</c>) and JS (<c>otlp-emitter</c>) both attach them on every request.
/// These tests drive the real pipeline against a loopback listener and inspect the received headers,
/// rather than asserting on config values that may go nowhere — which is exactly how the gap hid.
/// </remarks>
[Collection("EnvironmentVariables")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class LogHeaderTests
{
    [Fact]
    public void OtlpLogExport_SendsConfiguredLogGroupAndStreamHeaders()
    {
        var headers = CaptureExportHeaders(new()
        {
            ["OTEL_SERVICE_NAME"] = "log-header-explicit",
            ["OTEL_AWS_SERVICE_EVENTS_LOG_GROUP"] = "/my/group",
            ["OTEL_AWS_SERVICE_EVENTS_LOG_STREAM"] = "my-stream",
        });

        headers.Should().NotBeNull("the exporter should have sent at least one request");
        headers!["x-aws-log-group"].Should().Be("/my/group");
        headers["x-aws-log-stream"].Should().Be("my-stream");
    }

    [Fact]
    public void OtlpLogExport_WhenLogStreamUnset_FallsBackToServiceName()
    {
        var headers = CaptureExportHeaders(new()
        {
            ["OTEL_SERVICE_NAME"] = "log-header-fallback",

            // Neither log var set: the group takes its default and the stream takes the service
            // name, matching Java's OTEL_AWS_TELEMEND_LOG_STREAM fallback.
        });

        headers.Should().NotBeNull("the exporter should have sent at least one request");
        headers!["x-aws-log-group"].Should().Be(
            "/serviceevents/telemetry", "the documented default log group");
        headers["x-aws-log-stream"].Should().Be(
            "log-header-fallback", "an unset log stream must fall back to the service name");
    }

    /// <summary>Reserve a free loopback port by binding and immediately releasing it.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Bring ServiceEvents up against a loopback OTLP endpoint, flush, and return the headers of
    /// the first log-export request received.
    /// </summary>
    private static Dictionary<string, string>? CaptureExportHeaders(Dictionary<string, string> extraEnv)
    {
        var logsPort = FreePort();

        // A separate dead port for metrics: initialization requires both endpoints when
        // ServiceEvents is force-enabled without Application Signals, and pointing metrics at the
        // same listener would let a metrics request be mistaken for the log export.
        var metricsPort = FreePort();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{logsPort}/v1/logs/");
        listener.Start();

        Dictionary<string, string>? captured = null;
        using var received = new ManualResetEventSlim(false);

        var pump = Task.Run(() =>
        {
            try
            {
                var context = listener.GetContext();

                captured = context.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .ToDictionary(k => k!, k => context.Request.Headers[k] ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                context.Response.StatusCode = 200;
                context.Response.Close();
                received.Set();
            }
            catch (Exception)
            {
                // Listener stopped before a request arrived; the assertion on null reports it.
                received.Set();
            }
        });

        var env = new Dictionary<string, string>
        {
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
            ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
            ["RESOURCE_DETECTORS_ENABLED"] = "false",
            ["OTEL_AWS_OTLP_LOGS_ENDPOINT"] = $"http://127.0.0.1:{logsPort}/v1/logs",
            ["OTEL_AWS_OTLP_METRICS_ENDPOINT"] = $"http://127.0.0.1:{metricsPort}/v1/metrics",
        };

        foreach (var (key, value) in extraEnv)
        {
            env[key] = value;
        }

        try
        {
            // Isolate clears the whole influencing surface first, so OUTPUT_FILE (which would
            // bypass the OTLP exporter entirely) is unset regardless of the ambient environment.
            using (EnvScope.Isolate(env))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var inst = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
                inst.Initialize();
                inst.IsInitialized.Should().BeTrue("both endpoints are set, so initialization must proceed");

                // Dispose flushes the startup DeploymentEvent through the exporter.
                ServiceEventsInstrumentation.ResetForTests();
            }

            received.Wait(TimeSpan.FromSeconds(10));
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            listener.Stop();
            pump.Wait(TimeSpan.FromSeconds(5));
        }

        return captured;
    }
}

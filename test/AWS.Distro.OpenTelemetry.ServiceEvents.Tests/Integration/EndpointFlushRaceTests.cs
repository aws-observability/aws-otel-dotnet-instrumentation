// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// Regression test for the flush-boundary race in <c>EndpointMetricCollector</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>RecordRequest</c> resolves its aggregation from whatever the <c>aggregations</c> field pointed
/// at when it started. <c>Collect</c> replaces that field with <c>Interlocked.Exchange</c> and then
/// reads the detached map. A request that started just before the exchange writes into a map that
/// has already been read, and those requests are lost silently, once per flush.
/// </para>
/// <para>
/// Hitting that window by luck is unreliable — it is only tens of nanoseconds wide. Two things
/// widen it deliberately here: many distinct routes, so each <c>Collect</c> spends real time
/// iterating aggregations and building histograms while writers keep going, and sustained load for
/// a wall-clock interval rather than one burst, so hundreds of flushes overlap live traffic. The
/// invariant asserted is the one that matters — every request the test issued is accounted for in
/// some emitted window.
/// </para>
/// <para>
/// This is a stress test, so treat it as a guard rather than a proof. Measured against the unfixed
/// collector it detected the loss in roughly two runs out of three, dropping single-digit requests
/// out of ~12.8 million; with the fix it passed every time. So a green run does not prove the race
/// is gone, but a red one is always a real regression. It is deliberately volume-driven rather than
/// timing-assertion-driven, which keeps it from failing spuriously on a loaded CI machine.
/// </para>
/// </remarks>
[Collection("EnvironmentVariables")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class EndpointFlushRaceTests
{
    /// <summary>Distinct routes, so a flush has many aggregations to read while writers continue.</summary>
    private const int RouteCount = 250;

    private static readonly TimeSpan LoadDuration = TimeSpan.FromSeconds(2);

    [Fact]
    public void ConcurrentRequestsAcrossFlushBoundaries_AreNeverLost()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-flushrace-{Guid.NewGuid():N}.ndjson");

        try
        {
            long issued;

            using (EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "flush-race",

                // App Signals off so EndpointSummary is emitted and the counts are observable.
                ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",

                // Flush as fast as possible: every swap is another chance to drop a live write.
                ["OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL"] = "1",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var inst = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
                inst.Initialize();
                inst.IsInitialized.Should().BeTrue();

                var collector = inst.EndpointCollector;
                collector.Should().NotBeNull();

                issued = DriveSustainedLoad(collector!);

                // Final drain.
                ServiceEventsInstrumentation.ResetForTests();
            }

            var (windows, recorded) = ReadWindows(outputFile);

            issued.Should().BeGreaterThan(0, "the load generator must actually have run");

            // Without this guard the test is vacuous: if everything lands in one window there is no
            // swap to race against and any implementation passes.
            windows.Should().BeGreaterThan(
                10, "the writes must span many flushes for this test to exercise the swap");

            recorded.Should().Be(
                issued,
                "every recorded request must appear in some flushed window; requests that resolved " +
                "their aggregation just before a swap used to be written into the already-drained map");
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

    /// <summary>
    /// Hammer the collector from every core for <see cref="LoadDuration" />, returning the exact
    /// number of requests recorded.
    /// </summary>
    private static long DriveSustainedLoad(IEndpointRecorder collector)
    {
        var workers = Math.Max(4, Environment.ProcessorCount);
        var issued = 0L;
        var deadline = Environment.TickCount64 + (long)LoadDuration.TotalMilliseconds;

        var tasks = new Task[workers];

        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            tasks[w] = Task.Run(() =>
            {
                var local = 0L;
                var route = worker;

                while (Environment.TickCount64 < deadline)
                {
                    // Rotate routes so each flush has many aggregations to walk.
                    route = (route + 1) % RouteCount;

                    collector.RecordRequest(
                        $"/race/{route}",
                        "GET",
                        statusCode: 200,
                        durationNs: 1_000_000);

                    local++;
                }

                Interlocked.Add(ref issued, local);
            });
        }

        Task.WaitAll(tasks);

        return Interlocked.Read(ref issued);
    }

    /// <summary>
    /// Number of EndpointSummary windows emitted, and the total <c>request.count</c> across them.
    /// </summary>
    private static (int Windows, long Recorded) ReadWindows(string outputFile)
    {
        File.Exists(outputFile).Should().BeTrue("the file exporter should have written records");

        var windows = 0;
        var total = 0L;

        foreach (var line in File.ReadAllLines(outputFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var root = JsonDocument.Parse(line).RootElement;

            if (!root.TryGetProperty("eventName", out var name) ||
                name.GetString() != "aws.service_events.endpoint_summary")
            {
                continue;
            }

            windows++;

            if (root.TryGetProperty("attributes", out var attrs) &&
                attrs.TryGetProperty("aws.service_events.request.count", out var count))
            {
                total += count.GetInt64();
            }
        }

        return (windows, total);
    }
}

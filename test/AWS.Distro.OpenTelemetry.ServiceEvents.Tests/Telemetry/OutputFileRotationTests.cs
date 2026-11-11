// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Telemetry;

/// <summary>
/// Verifies the output file used by the <c>OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE</c> path is bounded.
/// </summary>
/// <remarks>
/// <para>
/// Before rotation existed the file grew for the life of the process, so the property under test is
/// containment: the active file is replaced once it reaches the cap, exactly one previous generation
/// survives, and nothing in the rotation path can fail an export.
/// </para>
/// <para>
/// The cap is passed explicitly throughout. The production default is 100 MB and writing that much to
/// exercise a rename would make these tests slow enough to be skipped, which is a worse outcome than
/// parameterising the bound.
/// </para>
/// </remarks>
public class OutputFileRotationTests
{
    /// <summary>
    /// At the cap, the current file is moved aside so the next append starts a fresh one.
    /// </summary>
    /// <remarks>
    /// Asserts the old bytes are intact in the rotated file, not merely that a second file exists.
    /// Rotation that discarded the content it rotated would satisfy the weaker assertion while
    /// destroying the evidence the file was turned on to collect.
    /// </remarks>
    [Fact]
    public void RotateIfOversized_AtTheCap_MovesTheFileAsideAndPreservesIt()
    {
        var path = NewTempPath();
        var previous = path + ".1";

        try
        {
            File.WriteAllText(path, "0123456789");

            ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(path, maxBytes: 10);

            File.Exists(path).Should().BeFalse("the oversized file is moved aside, not truncated in place");
            File.Exists(previous).Should().BeTrue("one previous generation is kept");
            File.ReadAllText(previous).Should().Be(
                "0123456789",
                "rotation must preserve what it rotates; the file exists to be read");
        }
        finally
        {
            Cleanup(path, previous);
        }
    }

    /// <summary>
    /// Under the cap, nothing moves.
    /// </summary>
    /// <remarks>
    /// The complement of the test above, and the one that fails if the comparison boundary is wrong.
    /// Rotating eagerly would still keep the file bounded, so a containment-only assertion would not
    /// notice; this pins that the common case leaves the file alone.
    /// </remarks>
    [Fact]
    public void RotateIfOversized_UnderTheCap_LeavesTheFileAlone()
    {
        var path = NewTempPath();
        var previous = path + ".1";

        try
        {
            File.WriteAllText(path, "short");

            ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(path, maxBytes: 1024);

            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Be("short");
            File.Exists(previous).Should().BeFalse("nothing was rotated, so no generation should exist");
        }
        finally
        {
            Cleanup(path, previous);
        }
    }

    /// <summary>
    /// A second rotation replaces the first generation rather than accumulating generations.
    /// </summary>
    /// <remarks>
    /// This is what actually bounds the disk. Rotation alone only bounds the <i>active</i> file; if each
    /// rotation left another file behind, total usage would still grow without limit and the finding
    /// would be unfixed. Two rotations is the smallest case that distinguishes the two.
    /// </remarks>
    [Fact]
    public void RotateIfOversized_OnSecondRotation_ReplacesTheGenerationRatherThanAccumulating()
    {
        var path = NewTempPath();
        var previous = path + ".1";

        try
        {
            File.WriteAllText(path, "generation-1");
            ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(path, maxBytes: 5);

            File.WriteAllText(path, "generation-2");
            ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(path, maxBytes: 5);

            File.Exists(path).Should().BeFalse();
            File.ReadAllText(previous).Should().Be(
                "generation-2",
                "the newer generation replaces the older one, bounding the path at two files");

            Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")
                .Should().HaveCount(1, "only one previous generation may survive a rotation");
        }
        finally
        {
            Cleanup(path, previous);
        }
    }

    /// <summary>
    /// A missing file is not an error.
    /// </summary>
    /// <remarks>
    /// This is the first-export case: rotation is called before every append, including the one that
    /// creates the file. It has to be a no-op rather than a throw, because it runs inside the exporter's
    /// write path.
    /// </remarks>
    [Fact]
    public void RotateIfOversized_WhenTheFileDoesNotExist_DoesNothing()
    {
        var path = NewTempPath();

        var act = () => ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(path, maxBytes: 1);

        act.Should().NotThrow();
        File.Exists(path + ".1").Should().BeFalse();
    }

    /// <summary>
    /// Rotation failure never propagates into the export path.
    /// </summary>
    /// <remarks>
    /// Driven by pointing rotation at a directory rather than a file: the size probe succeeds and the
    /// rename then fails, which is the shape of the real failures — a reader holding the file open, or a
    /// scanner mid-scan on Windows. Being unable to rename a debug file must not fail a telemetry export,
    /// so the contract is that the call returns quietly and the next flush tries again.
    /// </remarks>
    [Fact]
    public void RotateIfOversized_WhenTheRenameFails_DoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"se-rot-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "occupant.txt"), "keeps the directory non-empty");

        try
        {
            var act = () => ServiceEventsCloudWatchMetricFileExporter.RotateIfOversized(directory, maxBytes: 0);

            act.Should().NotThrow("a rotation failure is reported, not raised into the exporter");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// End to end through a real export: an oversized file is rotated and the export still lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tests above exercise the helper directly, which would keep passing if no exporter ever called
    /// it. This one drives a real <c>MeterProvider</c> through to the file, so it covers the wiring — and
    /// asserts the record still arrives, since a bounded file is no use if bounding it drops the write.
    /// </para>
    /// <para>
    /// Only the metric exporter is driven here. Its call site and the log exporter's are the same single
    /// call in the same position inside the same shared lock, and reaching the log path from a test would
    /// mean adding a logging package to this project to obtain a <c>LogRecord</c>, which has no public
    /// constructor.
    /// </para>
    /// <para>
    /// The file is grown by setting a length rather than writing bytes: the size probe reads the
    /// reported length, so this exercises the production 100 MB default without a 100 MB write.
    /// </para>
    /// </remarks>
    [Fact]
    public void Export_WhenTheFileIsOversized_RotatesItAndStillWritesTheRecord()
    {
        var path = NewTempPath();
        var previous = path + ".1";

        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(ServiceEventsCloudWatchMetricFileExporter.MaxOutputFileBytes + 1);
            }

            // A meter name unique to this test run, NOT the shared instrumentation scope name. Meter is
            // process-global: a provider subscribing to the shared name observes instruments created by
            // any concurrently-running test that also uses it, so a counter registered here would show up
            // in another test's output file and fail its assertion on which metric it found. The exporter
            // writes the scope name as a constant rather than reading it from the metric, so the name used
            // here does not affect what is emitted.
            var meterName = $"{ServiceEventsOtlpEmitter.InstrumentationScopeName}.rotation.{Guid.NewGuid():N}";

            using (var meter = new Meter(meterName, ServiceEventsOtlpEmitter.InstrumentationScopeVersion))
            {
                using var provider = Sdk.CreateMeterProviderBuilder()
                    .AddMeter(meterName)
                    .AddReader(new PeriodicExportingMetricReader(
                        new ServiceEventsCloudWatchMetricFileExporter(path),
                        exportIntervalMilliseconds: 600_000)
                    {
                        TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                    })
                    .Build();

                meter.CreateCounter<long>("count").Add(1, new TagList { { "status", "error" } });

                provider.ForceFlush();
            }

            File.Exists(previous).Should().BeTrue("the oversized file was rotated aside");

            var active = new FileInfo(path);
            active.Exists.Should().BeTrue("the export still has to land somewhere");
            active.Length.Should().BeInRange(
                1,
                ServiceEventsCloudWatchMetricFileExporter.MaxOutputFileBytes - 1,
                "the active file restarts from empty and then receives the exported batch");
        }
        finally
        {
            Cleanup(path, previous);
        }
    }

    private static string NewTempPath()
        => Path.Combine(Path.GetTempPath(), $"se-rot-{Guid.NewGuid():N}.ndjson");

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort: a leaked temp file must not fail the test that created it.
            }
        }
    }
}

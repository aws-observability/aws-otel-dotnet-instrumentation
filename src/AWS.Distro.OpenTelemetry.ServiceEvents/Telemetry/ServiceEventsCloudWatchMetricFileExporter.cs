// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// File-backed metrics exporter for the
/// <c>OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE</c> local-testing path. Writes one
/// canonical OTLP/JSON <c>ExportMetricsServiceRequest</c> per export batch,
/// sharing the output file with <see cref="ServiceEventsCloudWatchFileExporter" />.
/// </summary>
/// <remarks>
/// <para>
/// Output is native OTLP/JSON — the same shape the OTLP HTTP metrics exporter
/// sends on the wire.
/// There is <b>no</b> EMF <c>_aws</c> envelope and no metric-name capitalization —
/// the metric name stays lowercase (<c>count</c>) and the CloudWatch namespace is
/// assigned by the OTLP endpoint, not the SDK.
/// </para>
/// <para>
/// Emits the <c>count</c> Sum metric (EndpointErrorMetrics) and the
/// <c>service.function.duration</c> ExponentialHistogram (FunctionCall) as
/// native OTLP/JSON — a faithful mirror of the metrics wire.
/// </para>
/// <para>
/// Lock-protected writes share the file with the log exporter — both hold a
/// class-level lock keyed by the file path so NDJSON lines never interleave.
/// </para>
/// </remarks>
internal sealed class ServiceEventsCloudWatchMetricFileExporter : BaseExporter<Metric>
{
    /// <summary>
    /// Size at which the output file is rotated, in bytes.
    /// </summary>
    /// <remarks>
    /// Generous, because the file exists to be read by whoever turned it on and truncating their
    /// evidence early is its own failure. The number that matters is the total: one previous
    /// generation is kept, so the path is bounded at roughly twice this.
    /// </remarks>
    internal const long MaxOutputFileBytes = 100L * 1024 * 1024;

    // OTLP proto enum: AGGREGATION_TEMPORALITY_DELTA = 1. ServiceEvents metrics are Delta.
    private const int AggregationTemporalityDelta = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Cross-exporter file lock keyed by absolute path. Both the log and
    /// metric exporters acquire this same lock so writes from either side
    /// don't interleave a JSON line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is held <b>across the file write</b>, which couples the two exporters to each other and
    /// to the disk. They do not run on equivalent threads: the metric exporter is driven by a
    /// <c>PeriodicExportingMetricReader</c> on a background thread, while the log exporter is registered
    /// behind a <c>SimpleLogRecordExportProcessor</c> and therefore exports synchronously on whichever
    /// thread emitted the record. A write that stalls — a full disk, a hung network mount, a stopped
    /// container filesystem — holds this lock and blocks that emitting thread for as long as the stall
    /// lasts.
    /// </para>
    /// <para>
    /// That is accepted rather than fixed, and the bound is what makes it acceptable: this whole path
    /// exists only when <c>OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE</c> is set, which is opt-in, defaults to
    /// unset, and is documented as a local dev/test facility. The default configuration exports over the
    /// network and never constructs either file exporter.
    /// </para>
    /// <para>
    /// The real fix, were this ever to become a supported production sink, is to stop doing file I/O on
    /// the export path at all — hand records to a bounded queue drained by one dedicated writer thread,
    /// which also removes the need for a shared lock. That is a larger change than the exposure
    /// justifies today, so it is named here rather than half-built: a partial version, such as holding
    /// the lock for less of the write, would leave the same blocking behaviour while making it harder to
    /// see.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, object> FileLocks = new(StringComparer.Ordinal);

    private readonly string fullPath;
    private readonly object writeLock;
    private bool disposed;

    public ServiceEventsCloudWatchMetricFileExporter(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        this.fullPath = Path.GetFullPath(filePath);
        this.writeLock = GetOrCreateFileLock(this.fullPath);
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Metric> batch)
    {
        if (this.disposed)
        {
            return ExportResult.Failure;
        }

        // Build the scopeMetrics[].metrics[] array for everything in this batch.
        var metricsArray = new JsonArray();
        foreach (var metric in batch)
        {
            var metricNode = SerializeMetric(metric);
            if (metricNode is not null)
            {
                metricsArray.Add(metricNode);
            }
        }

        if (metricsArray.Count == 0)
        {
            // Nothing emittable in this batch — don't write an empty request line.
            return ExportResult.Success;
        }

        var request = new JsonObject
        {
            ["resourceMetrics"] = new JsonArray
            {
                new JsonObject
                {
                    ["resource"] = this.BuildResourceNode(),
                    ["scopeMetrics"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["scope"] = new JsonObject
                            {
                                ["name"] = ServiceEventsOtlpEmitter.InstrumentationScopeName,
                                ["version"] = ServiceEventsOtlpEmitter.InstrumentationScopeVersion,
                            },
                            ["metrics"] = metricsArray,
                        },
                    },
                },
            },
        };

        // Append under the shared per-file lock (same lock the log exporter uses) so
        // NDJSON lines never interleave. Open-append-close per flush avoids the
        // dual-buffer corruption that two persistent StreamWriters on one file cause.
        lock (this.writeLock)
        {
            RotateIfOversized(this.fullPath);

            try
            {
                File.AppendAllText(this.fullPath, request.ToJsonString(SerializerOptions) + "\n");
                return ExportResult.Success;
            }
            catch (Exception ex)
            {
                ServiceEventsEventSource.Log.FileWriteFailed(this.fullPath, ex);
                return ExportResult.Failure;
            }
        }
    }

    /// <summary>
    /// Get the shared lock object for a given output file path. Same path →
    /// same lock instance, even across log + metric exporters.
    /// </summary>
    internal static object GetOrCreateFileLock(string fullPath)
    {
        lock (FileLocks)
        {
            if (!FileLocks.TryGetValue(fullPath, out var lockObj))
            {
                lockObj = new object();
                FileLocks[fullPath] = lockObj;
            }

            return lockObj;
        }
    }

    /// <summary>
    /// Rotate the output file if it has reached <paramref name="maxBytes" />, keeping one previous
    /// generation alongside it. No-op when the file is absent or still under the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the file grows for the life of the process. A long soak or a left-on debug setting
    /// eventually fills the volume, and that harms the host rather than just this feature: on a shared
    /// filesystem the process that fails first is whichever one next needs to write, which may well be
    /// the application being instrumented. Telemetry is not entitled to take the disk with it.
    /// </para>
    /// <para>
    /// One previous generation, replaced each time, bounds the path at roughly twice the cap. Keeping N
    /// generations would need a shift of N files per rotation under the shared write lock, for a
    /// dev/test artefact where the recent tail is the part anyone reads.
    /// </para>
    /// <para>
    /// Callers <b>must</b> already hold the lock from <see cref="GetOrCreateFileLock" /> for this path.
    /// Rotation reads a size and renames a file, and doing either concurrently with an append from the
    /// other exporter would race — the rename could land between another writer's size check and its
    /// write, sending that write to the rotated-away file.
    /// </para>
    /// <para>
    /// Deliberately total: any failure is reported and swallowed. A rename can lose to a virus scanner
    /// or a reader holding the file open on Windows, and neither losing the export nor throwing into the
    /// SDK's export path is a proportionate response to being unable to rename a debug file. Continuing
    /// to append leaves the file oversized, which is the same state as before this existed, and the next
    /// flush tries again.
    /// </para>
    /// </remarks>
    /// <param name="fullPath">Absolute path to the output file.</param>
    /// <param name="maxBytes">Size at which to rotate. Overridable so tests need not write 100 MB.</param>
    internal static void RotateIfOversized(string fullPath, long maxBytes = MaxOutputFileBytes)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length < maxBytes)
            {
                return;
            }

            // Size is checked before appending rather than predicted from the pending batch, so the
            // file can exceed the cap by at most one batch. Bounding it exactly would mean measuring
            // every serialized batch before writing it, to hold a limit that is already approximate.
            File.Move(fullPath, fullPath + ".1", overwrite: true);
            ServiceEventsEventSource.Log.OutputFileRotated(fullPath, info.Length);
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.FileWriteFailed(fullPath, ex);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        this.disposed = true;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Serialize one <see cref="Metric"/> into an OTLP/JSON metric node, or null
    /// when the metric carries no emittable data points.
    /// </summary>
    /// <remarks>
    /// Handles the <c>count</c> Sum metric (EndpointErrorMetrics) and the
    /// <c>service.function.duration</c> ExponentialHistogram (FunctionCall).
    /// </remarks>
    private static JsonNode? SerializeMetric(Metric metric)
    {
        switch (metric.MetricType)
        {
            case MetricType.LongSum:
            case MetricType.DoubleSum:
                return SerializeSum(metric);

            case MetricType.ExponentialHistogram:
                return SerializeExponentialHistogram(metric);

            default:
                return null;
        }
    }

    /// <summary>Serialize a Sum metric (the <c>count</c> EndpointErrorMetrics metric).</summary>
    private static JsonNode? SerializeSum(Metric metric)
    {
        var dataPoints = new JsonArray();
        var isLong = metric.MetricType == MetricType.LongSum;

        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var dp = new JsonObject
            {
                ["attributes"] = SerializeAttributes(in point),
                ["startTimeUnixNano"] = ToUnixNanos(point.StartTime).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["timeUnixNano"] = ToUnixNanos(point.EndTime).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            if (isLong)
            {
                // OTLP/JSON encodes int64 as a string.
                dp["asInt"] = point.GetSumLong().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                dp["asDouble"] = point.GetSumDouble();
            }

            dataPoints.Add(dp);
        }

        if (dataPoints.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["name"] = metric.Name,
            ["unit"] = string.IsNullOrEmpty(metric.Unit) ? "Count" : metric.Unit,
            ["sum"] = new JsonObject
            {
                ["aggregationTemporality"] = AggregationTemporalityDelta,
                ["isMonotonic"] = true,
                ["dataPoints"] = dataPoints,
            },
        };
    }

    /// <summary>
    /// Serialize an ExponentialHistogram metric (the <c>service.function.duration</c>
    /// FunctionCall metric) as a base-2 exponential histogram OTLP/JSON node.
    /// </summary>
    private static JsonNode? SerializeExponentialHistogram(Metric metric)
    {
        var dataPoints = new JsonArray();

        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var expo = point.GetExponentialHistogramData();

            var dp = new JsonObject
            {
                ["attributes"] = SerializeAttributes(in point),
                ["startTimeUnixNano"] = ToUnixNanos(point.StartTime).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["timeUnixNano"] = ToUnixNanos(point.EndTime).ToString(System.Globalization.CultureInfo.InvariantCulture),

                // count + zeroCount are uint64 on the wire → OTLP/JSON encodes them as strings.
                ["count"] = point.GetHistogramCount().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sum"] = point.GetHistogramSum(),
                ["scale"] = expo.Scale,
                ["zeroCount"] = expo.ZeroCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["positive"] = SerializeBuckets(expo.PositiveBuckets),
            };

            // RecordMinMax is on by default for histograms; mirror what the wire carries.
            if (point.TryGetHistogramMinMaxValues(out var min, out var max))
            {
                dp["min"] = min;
                dp["max"] = max;
            }

            dataPoints.Add(dp);
        }

        if (dataPoints.Count == 0)
        {
            return null;
        }

        var node = new JsonObject
        {
            ["name"] = metric.Name,
            ["unit"] = string.IsNullOrEmpty(metric.Unit) ? "Microseconds" : metric.Unit,
        };

        if (!string.IsNullOrEmpty(metric.Description))
        {
            node["description"] = metric.Description;
        }

        node["exponentialHistogram"] = new JsonObject
        {
            ["aggregationTemporality"] = AggregationTemporalityDelta,
            ["dataPoints"] = dataPoints,
        };

        return node;
    }

    /// <summary>
    /// Serialize the positive buckets of an exponential histogram as the OTLP
    /// <c>buckets</c> shape: an <c>offset</c> plus an array of uint64
    /// <c>bucketCounts</c> (each encoded as a string per OTLP/JSON).
    /// </summary>
    private static JsonObject SerializeBuckets(ExponentialHistogramBuckets buckets)
    {
        var counts = new JsonArray();
        foreach (var count in buckets)
        {
            counts.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new JsonObject
        {
            ["offset"] = buckets.Offset,
            ["bucketCounts"] = counts,
        };
    }

    /// <summary>Convert a <see cref="DateTimeOffset"/> to Unix nanoseconds (ms precision × 1e6).</summary>
    private static long ToUnixNanos(DateTimeOffset time) => time.ToUnixTimeMilliseconds() * 1_000_000L;

    /// <summary>Serialize a metric point's tags as an OTLP/JSON attributes array.</summary>
    private static JsonArray SerializeAttributes(in MetricPoint point)
    {
        var attrs = new JsonArray();
        foreach (var tag in point.Tags)
        {
            attrs.Add(new JsonObject
            {
                ["key"] = tag.Key,
                ["value"] = OtlpAnyValue(tag.Value),
            });
        }

        return attrs;
    }

    /// <summary>Wrap a tag value in the OTLP <c>AnyValue</c> JSON shape.</summary>
    private static JsonObject OtlpAnyValue(object? value) => value switch
    {
        null => new JsonObject { ["stringValue"] = string.Empty },
        bool b => new JsonObject { ["boolValue"] = b },
        long l => new JsonObject { ["intValue"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        int i => new JsonObject { ["intValue"] = ((long)i).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        double d => new JsonObject { ["doubleValue"] = d },
        float f => new JsonObject { ["doubleValue"] = (double)f },
        _ => new JsonObject { ["stringValue"] = value.ToString() ?? string.Empty },
    };

    /// <summary>Build the OTLP <c>resource</c> node from the exporter's parent provider resource.</summary>
    private JsonObject BuildResourceNode()
    {
        var attrs = new JsonArray();
        var resource = this.ParentProvider?.GetResource();
        if (resource is not null)
        {
            foreach (var attr in resource.Attributes)
            {
                attrs.Add(new JsonObject
                {
                    ["key"] = attr.Key,
                    ["value"] = OtlpAnyValue(attr.Value),
                });
            }
        }

        return new JsonObject { ["attributes"] = attrs };
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Emits the <c>count</c> Sum metric (EndpointErrorMetrics, §7) and the
/// <c>service.function.duration</c> ExponentialHistogram (FunctionCall, §4) as
/// native OTLP/JSON — a faithful mirror of the metrics wire.
/// </para>
/// <para>
/// Lock-protected writes share the file with the log exporter — both hold a
/// class-level lock keyed by the file path so NDJSON lines never interleave.
/// </para>
/// </remarks>
internal sealed class ServiceEventsCloudWatchMetricFileExporter : BaseExporter<Metric>
{
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
            try
            {
                File.AppendAllText(this.fullPath, request.ToJsonString(SerializerOptions) + "\n");
                return ExportResult.Success;
            }
            catch
            {
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
    /// Handles the <c>count</c> Sum metric (EndpointErrorMetrics, §7) and the
    /// <c>service.function.duration</c> ExponentialHistogram (FunctionCall, §4).
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

    /// <summary>Serialize a Sum metric (the <c>count</c> EndpointErrorMetrics metric, spec §7).</summary>
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
    /// FunctionCall metric, spec §4) as a base-2 exponential histogram OTLP/JSON node.
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

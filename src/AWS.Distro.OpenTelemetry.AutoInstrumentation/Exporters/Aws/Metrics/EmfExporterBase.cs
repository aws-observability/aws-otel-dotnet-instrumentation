// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics
{
    /// <summary>
    /// Intermediate format for metric data before converting to EMF.
    /// </summary>
    public class MetricRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
        public double? Value { get; set; }
        public HistogramMetricRecordData? HistogramData { get; set; }
        public ExponentialHistogramMetricRecordData? ExpHistogramData { get; set; }
    }

    /// <summary>
    /// Histogram metric record data.
    /// </summary>
    public class HistogramMetricRecordData
    {
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
    }

    /// <summary>
    /// Exponential histogram metric record data.
    /// </summary>
    public class ExponentialHistogramMetricRecordData
    {
        public double[] Values { get; set; } = Array.Empty<double>();
        public long[] Counts { get; set; } = Array.Empty<long>();
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
    }

    /// <summary>
    /// EMF log structure.
    /// </summary>
    public class EmfLog : Dictionary<string, object>
    {
        public EmfLog()
        {
            this["Version"] = "1";
        }
    }

    /// <summary>
    /// CloudWatch metric definition.
    /// </summary>
    public class CloudWatchMetric
    {
        public string Namespace { get; set; } = string.Empty;
        public string[][]? Dimensions { get; set; }
        public MetricDefinition[] Metrics { get; set; } = Array.Empty<MetricDefinition>();
    }

    /// <summary>
    /// Metric definition.
    /// </summary>
    public class MetricDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? Unit { get; set; }
    }

    /// <summary>
    /// AWS EMF metadata.
    /// </summary>
    public class AwsMetadata
    {
        public long Timestamp { get; set; }
        public CloudWatchMetric[] CloudWatchMetrics { get; set; } = Array.Empty<CloudWatchMetric>();
    }

    /// <summary>
    /// Log event structure.
    /// </summary>
    public class LogEvent
    {
        public string Message { get; set; } = string.Empty;
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Base class for OpenTelemetry metrics exporters that convert to CloudWatch EMF format.
    /// </summary>
    public abstract class EmfExporterBase : BaseExporter<Metric>
    {
        private readonly string _namespace;
        private readonly HashSet<string> _emfSupportedUnits;
        private readonly Dictionary<string, string> _unitMapping;

        protected EmfExporterBase(string namespaceName = "default")
        {
            _namespace = namespaceName;
            
            _emfSupportedUnits = new HashSet<string>
            {
                "Seconds", "Microseconds", "Milliseconds", "Bytes", "Kilobytes", "Megabytes",
                "Gigabytes", "Terabytes", "Bits", "Kilobits", "Megabits", "Gigabits", "Terabits",
                "Percent", "Count", "Bytes/Second", "Kilobytes/Second", "Megabytes/Second",
                "Gigabytes/Second", "Terabytes/Second", "Bits/Second", "Kilobits/Second",
                "Megabits/Second", "Gigabits/Second", "Terabits/Second", "Count/Second", "None"
            };

            _unitMapping = new Dictionary<string, string>
            {
                { "1", "" },
                { "ns", "" },
                { "ms", "Milliseconds" },
                { "s", "Seconds" },
                { "us", "Microseconds" },
                { "By", "Bytes" },
                { "bit", "Bits" }
            };
        }

        /// <summary>
        /// Normalize an OpenTelemetry timestamp to milliseconds for CloudWatch.
        /// </summary>
        private long NormalizeTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Create a base metric record with instrument information.
        /// </summary>
        private MetricRecord CreateMetricRecord(
            string metricName,
            string metricUnit,
            string metricDescription,
            long timestamp,
            Dictionary<string, object> attributes)
        {
            return new MetricRecord
            {
                Name = metricName,
                Unit = metricUnit,
                Description = metricDescription,
                Timestamp = timestamp,
                Attributes = attributes
            };
        }

        /// <summary>
        /// Convert a Gauge or Sum metric datapoint to a metric record.
        /// </summary>
        private MetricRecord ConvertGaugeAndSum(Metric metric, ref readonly MetricPoint metricPoint)
        {
            var timestampMs = NormalizeTimestamp(metricPoint.EndTime);
            var attributes = new Dictionary<string, object>();
            foreach (var tag in metricPoint.Tags)
            {
                attributes[tag.Key] = tag.Value ?? string.Empty;
            }

            var record = CreateMetricRecord(
                metric.Name,
                metric.Unit ?? string.Empty,
                metric.Description ?? string.Empty,
                timestampMs,
                attributes);

            record.Value = metric.MetricType switch
            {
                MetricType.LongSum or MetricType.LongGauge or MetricType.LongSumNonMonotonic or MetricType.LongGaugeNonMonotonic => metricPoint.GetSumLong(),
                MetricType.DoubleSum or MetricType.DoubleGauge or MetricType.DoubleSumNonMonotonic or MetricType.DoubleGaugeNonMonotonic => metricPoint.GetSumDouble(),
                _ => 0
            };

            return record;
        }

        /// <summary>
        /// Convert a Histogram metric datapoint to a metric record.
        /// </summary>
        private MetricRecord ConvertHistogram(Metric metric, ref readonly MetricPoint metricPoint)
        {
            var timestampMs = NormalizeTimestamp(metricPoint.EndTime);
            var attributes = new Dictionary<string, object>();
            foreach (var tag in metricPoint.Tags)
            {
                attributes[tag.Key] = tag.Value ?? string.Empty;
            }

            var record = CreateMetricRecord(
                metric.Name,
                metric.Unit ?? string.Empty,
                metric.Description ?? string.Empty,
                timestampMs,
                attributes);

            var min = 0.0;
            var max = 0.0;
            if (metricPoint.TryGetHistogramMinMaxValues(out var minValue, out var maxValue))
            {
                min = minValue;
                max = maxValue;
            }

            record.HistogramData = new HistogramMetricRecordData
            {
                Count = metricPoint.GetHistogramCount(),
                Sum = metricPoint.GetHistogramSum(),
                Min = min,
                Max = max
            };

            return record;
        }

        /// <summary>
        /// Convert an ExponentialHistogram metric datapoint to a metric record.
        /// </summary>
        private MetricRecord ConvertExpHistogram(Metric metric, ref readonly MetricPoint metricPoint)
        {
            var arrayValues = new List<double>();
            var arrayCounts = new List<long>();

            var timestampMs = NormalizeTimestamp(metricPoint.EndTime);
            var attributes = new Dictionary<string, object>();
            foreach (var tag in metricPoint.Tags)
            {
                attributes[tag.Key] = tag.Value ?? string.Empty;
            }

            var expHistogram = metricPoint.GetExponentialHistogramData();
            var scale = expHistogram.Scale;
            var baseValue = Math.Pow(2, Math.Pow(2, -scale));

            // Process positive buckets using reflection
            if (expHistogram.PositiveBuckets != null)
            {
                var positiveBucketsType = expHistogram.PositiveBuckets.GetType();
                var offsetProperty = positiveBucketsType.GetProperty("Offset");
                var positiveOffset = (int)(offsetProperty?.GetValue(expHistogram.PositiveBuckets) ?? 0);
                
                var bucketIndex = 0;
                var enumerator = expHistogram.PositiveBuckets.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var bucketCount = enumerator.Current;
                    var index = bucketIndex + positiveOffset;
                    var bucketBegin = Math.Pow(baseValue, index);
                    var bucketEnd = Math.Pow(baseValue, index + 1);
                    var metricVal = (bucketBegin + bucketEnd) / 2;

                    if (bucketCount > 0)
                    {
                        arrayValues.Add(metricVal);
                        arrayCounts.Add((long)bucketCount);
                    }
                    
                    bucketIndex++;
                }
            }

            // Process zero bucket
            var zeroCount = expHistogram.ZeroCount;
            if (zeroCount > 0)
            {
                arrayValues.Add(0);
                arrayCounts.Add(zeroCount);
            }

            // Process negative buckets using reflection
            var negativeBucketsProperty = expHistogram.GetType().GetProperty("NegativeBuckets");
            var negativeBuckets = negativeBucketsProperty?.GetValue(expHistogram);
            
            if (negativeBuckets != null)
            {
                var negativeBucketsType = negativeBuckets.GetType();
                var offsetProperty = negativeBucketsType.GetProperty("Offset");
                var negativeOffset = (int)(offsetProperty?.GetValue(negativeBuckets) ?? 0);
                
                var getEnumeratorMethod = negativeBucketsType.GetMethod("GetEnumerator");
                var enumerator = getEnumeratorMethod?.Invoke(negativeBuckets, null);
                
                if (enumerator != null)
                {
                    var moveNextMethod = enumerator.GetType().GetMethod("MoveNext");
                    var currentProperty = enumerator.GetType().GetProperty("Current");
                    
                    var bucketIndex = 0;
                    while ((bool)(moveNextMethod?.Invoke(enumerator, null) ?? false))
                    {
                        var bucketCount = Convert.ToInt64(currentProperty?.GetValue(enumerator));
                        var index = bucketIndex + negativeOffset;
                        var bucketEnd = -Math.Pow(baseValue, index);
                        var bucketBegin = -Math.Pow(baseValue, index + 1);
                        var metricVal = (bucketBegin + bucketEnd) / 2;

                        if (bucketCount > 0)
                        {
                            arrayValues.Add(metricVal);
                            arrayCounts.Add(bucketCount);
                        }
                        
                        bucketIndex++;
                    }
                }
            }
            
            var record = CreateMetricRecord(
                metric.Name,
                metric.Unit ?? string.Empty,
                metric.Description ?? string.Empty,
                timestampMs,
                attributes);

            var min = 0.0;
            var max = 0.0;
            if (metricPoint.TryGetHistogramMinMaxValues(out var minValue, out var maxValue))
            {
                min = minValue;
                max = maxValue;
            }

            record.ExpHistogramData = new ExponentialHistogramMetricRecordData
            {
                Values = arrayValues.ToArray(),
                Counts = arrayCounts.ToArray(),
                Count = (long)metricPoint.GetHistogramCount(),
                Sum = metricPoint.GetHistogramSum(),
                Max = max,
                Min = min
            };

            return record;
        }

        /// <summary>
        /// Group metric record by attributes and timestamp.
        /// </summary>
        private (string, long) GroupByAttributesAndTimestamp(MetricRecord record)
        {
            var attrsKey = GetAttributesKey(record.Attributes);
            return (attrsKey, record.Timestamp);
        }

        /// <summary>
        /// Method to handle safely pushing a MetricRecord into a Map of a Map of a list of MetricRecords.
        /// </summary>
        private void PushMetricRecordIntoGroupedMetrics(
            Dictionary<string, Dictionary<long, List<MetricRecord>>> groupedMetrics,
            string groupAttribute,
            long groupTimestamp,
            MetricRecord record)
        {
            if (!groupedMetrics.ContainsKey(groupAttribute))
            {
                groupedMetrics[groupAttribute] = new Dictionary<long, List<MetricRecord>>();
            }

            if (!groupedMetrics[groupAttribute].ContainsKey(groupTimestamp))
            {
                groupedMetrics[groupAttribute][groupTimestamp] = new List<MetricRecord>();
            }

            groupedMetrics[groupAttribute][groupTimestamp].Add(record);
        }

        /// <summary>
        /// Get CloudWatch unit from unit in MetricRecord.
        /// </summary>
        private string? GetUnit(MetricRecord record)
        {
            var unit = record.Unit;
            
            if (_emfSupportedUnits.Contains(unit))
            {
                return unit;
            }

            return _unitMapping.TryGetValue(unit, out var mappedUnit) ? mappedUnit : null;
        }

        /// <summary>
        /// Extract dimension names from attributes.
        /// </summary>
        private string[] GetDimensionNames(Dictionary<string, object> attributes)
        {
            return attributes.Keys.ToArray();
        }

        /// <summary>
        /// Create a hashable key from attributes for grouping metrics.
        /// </summary>
        private string GetAttributesKey(Dictionary<string, object> attributes)
        {
            var sortedAttrs = attributes.OrderBy(kvp => kvp.Key).ToArray();
            return string.Join(",", sortedAttrs.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        /// <summary>
        /// Create EMF log from metric records.
        /// </summary>
        private EmfLog CreateEmfLog(List<MetricRecord> metricRecords, Resource resource, long? timestamp = null)
        {
            var emfLog = new EmfLog();
            
            var awsMetadata = new AwsMetadata
            {
                Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = Array.Empty<CloudWatchMetric>()
            };

            emfLog["_aws"] = awsMetadata;

            // Add resource attributes
            if (resource?.Attributes != null)
            {
                foreach (var attr in resource.Attributes)
                {
                    emfLog[$"otel.resource.{attr.Key}"] = attr.Value?.ToString() ?? "undefined";
                }
            }

            var metricDefinitions = new List<MetricDefinition>();
            var allAttributes = metricRecords.Count > 0 ? metricRecords[0].Attributes : new Dictionary<string, object>();

            // Process each metric record
            foreach (var record in metricRecords)
            {
                if (string.IsNullOrEmpty(record.Name))
                    continue;

                if (record.ExpHistogramData != null)
                {
                    emfLog[record.Name] = record.ExpHistogramData;
                }
                else if (record.HistogramData != null)
                {
                    emfLog[record.Name] = record.HistogramData;
                }
                else if (record.Value.HasValue)
                {
                    emfLog[record.Name] = record.Value.Value;
                }
                else
                {
                    //[] Debug Log here diag.debug(`Skipping metric ${metricName} as it does not have valid metric value`);
                    continue;
                }

                var metricDef = new MetricDefinition { Name = record.Name };
                var unit = GetUnit(record);
                if (!string.IsNullOrEmpty(unit))
                {
                    metricDef.Unit = unit;
                }
                metricDefinitions.Add(metricDef);
            }

            var dimensionNames = GetDimensionNames(allAttributes);

            // Add attribute values to the root of the EMF log
            foreach (var attr in allAttributes)
            {
                emfLog[attr.Key] = attr.Value?.ToString() ?? "undefined";
            }

            // Add CloudWatch Metrics
            if (metricDefinitions.Count > 0)
            {
                var cloudWatchMetric = new CloudWatchMetric
                {
                    Namespace = _namespace,
                    Metrics = metricDefinitions.ToArray()
                };

                if (dimensionNames.Length > 0)
                {
                    cloudWatchMetric.Dimensions = new[] { dimensionNames };
                }

                awsMetadata.CloudWatchMetrics = new[] { cloudWatchMetric };
            }

            return emfLog;
        }

        /// <summary>
        /// Export metrics as EMF logs.
        /// Groups metrics by attributes and timestamp before creating EMF logs.
        /// </summary>
        public override ExportResult Export(in Batch<Metric> batch)
        {
            Console.WriteLine($"\nEMF Export called with {batch.Count} metrics.");
            File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] EMF Export called with {batch.Count} metrics\n");
            try
            {
                var resource = ParentProvider?.GetResource() ?? Resource.Empty;
                var groupedMetrics = new Dictionary<string, Dictionary<long, List<MetricRecord>>>();

                var metricNames = new List<string>();
                foreach (var metric in batch)
                {
                    metricNames.Add($"{metric.Name}({metric.MeterName})");
                }
                Console.WriteLine($"All metrics in batch: {string.Join(", ", metricNames)}");
                File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] All metrics: {string.Join(", ", metricNames)}\n");
                
                foreach (var metric in batch)
                {
                    try
                    {
                        Console.WriteLine($"Processing metric: {metric.Name} from meter: {metric.MeterName} (type: {metric.MetricType})");
                        File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] Processing metric: {metric.Name} from meter: {metric.MeterName} (type: {metric.MetricType})\n");
                        
                        if (metric.MeterName == "dice-lib")
                        {
                            Console.WriteLine($"*** FOUND DICE-LIB METRIC: {metric.Name} ***");
                            File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] *** FOUND DICE-LIB METRIC: {metric.Name} ***\n");
                        }
                        
                        foreach (var metricPoint in metric.GetMetricPoints())
                        {
                            MetricRecord record = metric.MetricType switch
                            {
                                MetricType.LongSum or MetricType.LongGauge or MetricType.DoubleSum or MetricType.DoubleGauge or MetricType.LongSumNonMonotonic or MetricType.DoubleSumNonMonotonic or MetricType.LongGaugeNonMonotonic or MetricType.DoubleGaugeNonMonotonic => ConvertGaugeAndSum(metric, in metricPoint),
                                MetricType.Histogram => ConvertHistogram(metric, in metricPoint),
                                MetricType.ExponentialHistogram => ConvertExpHistogram(metric, in metricPoint),
                                _ => throw new NotSupportedException($"Unsupported metric type: {metric.MetricType}")
                            };

                            var (groupAttribute, groupTimestamp) = GroupByAttributesAndTimestamp(record);
                            PushMetricRecordIntoGroupedMetrics(groupedMetrics, groupAttribute, groupTimestamp, record);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing metric {metric.Name}: {ex.Message}");
                        File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] Error processing metric {metric.Name}: {ex.Message}\n");
                    }
                }
                
                Console.WriteLine($"Finished processing all {batch.Count} metrics. Creating EMF logs...");
                File.AppendAllText("/app/logs/plugin-debug.log", $"[{DateTime.Now}] Finished processing all {batch.Count} metrics. Creating EMF logs...\n");

                // Process each group separately to create one EMF log per group
                foreach (var metricsRecordsGroupedByTimestamp in groupedMetrics.Values)
                {
                    foreach (var (timestampMs, metricRecords) in metricsRecordsGroupedByTimestamp)
                    {
                        if (metricRecords != null)
                        {
                            var logEvent = new LogEvent
                            {
                                Message = JsonSerializer.Serialize(CreateEmfLog(metricRecords, resource, timestampMs)),
                                Timestamp = timestampMs
                            };

                            SendLogEventAsync(logEvent).GetAwaiter().GetResult();
                        }
                    }
                }

                return ExportResult.Success;
            }
            catch (Exception)
            {
                //[] diag.error(`Failed to export metrics: ${e}`);
                return ExportResult.Failure;
            }
        }



        /// <summary>
        /// Send a log event to the destination.
        /// </summary>
        protected abstract Task SendLogEventAsync(LogEvent logEvent);

        /// <summary>
        /// Force flush any pending metrics.
        /// </summary>
        public abstract Task ForceFlushAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Shutdown the exporter.
        /// </summary>
        public abstract Task ShutdownAsync(CancellationToken cancellationToken);

    }
}
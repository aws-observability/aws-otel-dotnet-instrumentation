// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsSpanMetricsProcessor is SpanProcessor that generates metrics from spans
/// This processor will generate metrics based on span data. It depends on a MetricAttributeGenerator being provided on
/// instantiation, which will provide a means to determine attributes which should be used to create metrics. A Resource
/// must also be provided, which is used to generate metrics.Finally, three Histogram must be provided, which will be
/// used to actually create desired metrics (see below)
///
/// AwsSpanMetricsProcessor produces metrics for errors (e.g.HTTP 4XX status codes), faults(e.g.HTTP 5XX status
/// codes), and latency(in Milliseconds). Errors and faults are counted, while latency is measured with a histogram.
/// Metrics are emitted with attributes derived from span attributes.
///
/// For highest fidelity metrics, this processor should be coupled with the AlwaysRecordSampler, which will result in
/// 100% of spans being sent to the processor.
/// </summary>
public class AwsSpanMetricsProcessor : BaseProcessor<Activity>
{
    private const double NanosToMillis = 1_000_000.0;

    // Constants for deriving error and fault metrics
    private const int ErrorCodeLowerBound = 400;
    private const int ErrorCodeUpperBound = 499;
    private const int FaultCodeLowerBound = 500;
    private const int FaultCodeUpperBound = 599;

    // Metric instruments
    private Histogram<long> errorHistogram;
    private Histogram<long> faultHistogram;
    private Histogram<double> latencyHistogram;

    private IMetricAttributeGenerator generator;
    private Resource resource;

    private AwsSpanMetricsProcessor(
      Histogram<long> errorHistogram,
      Histogram<long> faultHistogram,
      Histogram<double> latencyHistogram,
      IMetricAttributeGenerator generator,
      Resource resource)
    {
        this.errorHistogram = errorHistogram;
        this.faultHistogram = faultHistogram;
        this.latencyHistogram = latencyHistogram;
        this.generator = generator;
        this.resource = resource;
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
        Console.WriteLine($"OnEnd: {activity.DisplayName}");
    }

    /// <summary>
    /// Use <see cref="AwsSpanMetricsProcessorBuilder"/> to construct this processor
    /// </summary>
    /// <returns>Configured AwsSpanMetricsProcessor</returns>
    internal static AwsSpanMetricsProcessor Create(
        Histogram<long> errorHistogram,
        Histogram<long> faultHistogram,
        Histogram<double> latencyHistogram,
        IMetricAttributeGenerator generator,
        Resource resource)
    {
        return new AwsSpanMetricsProcessor(
            errorHistogram, faultHistogram, latencyHistogram, generator, resource);
    }

    // The logic to record error and fault should be kept in sync with the aws-xray exporter whenever
    // possible except for the throttle
    // https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/awsxrayexporter/internal/translator/cause.go#L121-L160
    private void RecordErrorOrFault(Activity span, ActivityTagsCollection attributes)
    {
        KeyValuePair<string, object?>[] attributesArray = attributes.ToArray();
        object? httpStatusCode = span.GetTagItem(AttributeHttpStatusCode);
        ActivityStatusCode statusCode = span.Status;

        if (httpStatusCode == null)
        {
            attributes.TryGetValue(AttributeHttpStatusCode, out httpStatusCode);
        }

        if (httpStatusCode == null
            || (long)httpStatusCode < ErrorCodeLowerBound
            || (long)httpStatusCode > FaultCodeUpperBound)
        {
            if (ActivityStatusCode.Error.Equals(statusCode))
            {
                this.errorHistogram.Record(0, attributesArray);
                this.faultHistogram.Record(1, attributesArray);
            }
            else
            {
                this.errorHistogram.Record(0, attributesArray);
                this.faultHistogram.Record(0, attributesArray);
            }
        }
        else if ((long)httpStatusCode >= ErrorCodeLowerBound
            && (long)httpStatusCode <= ErrorCodeUpperBound)
        {
            this.errorHistogram.Record(1, attributesArray);
            this.faultHistogram.Record(0, attributesArray);
        }
        else if ((long)httpStatusCode >= FaultCodeLowerBound
            && (long)httpStatusCode <= FaultCodeUpperBound)
        {
            this.errorHistogram.Record(0, attributesArray);
            this.faultHistogram.Record(1, attributesArray);
        }
    }

    private void RecordLatency(Activity span, ActivityTagsCollection attributes)
    {
        double millis = span.Duration.TotalMilliseconds;
        this.latencyHistogram.Record(millis, attributes.ToArray());
    }

    private void RecordMetrics(Activity span, ActivityTagsCollection attributes)
    {
        // Only record metrics if non-empty attributes are returned.
        if (attributes.Count > 0)
        {
            this.RecordErrorOrFault(span, attributes);
            this.RecordLatency(span, attributes);
        }
    }
}

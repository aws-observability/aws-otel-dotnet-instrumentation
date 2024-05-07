// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Authentication.ExtendedProtection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
        Console.WriteLine($"OnStart: {activity.DisplayName}");
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
        Console.WriteLine($"OnEnd: {activity.DisplayName}");
    }
}

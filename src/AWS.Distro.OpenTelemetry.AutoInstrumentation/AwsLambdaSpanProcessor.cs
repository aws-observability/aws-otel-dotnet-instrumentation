// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributesSpanProcessor is SpanProcessor that generates metrics from spans
/// This processor will generate metrics based on span data. It depends on a MetricAttributeGenerator being provided on
/// instantiation, which will provide a means to determine attributes which should be used to create metrics. A Resource
/// must also be provided, which is used to generate metrics.Finally, three Histogram must be provided, which will be
/// used to actually create desired metrics (see below)
///
/// AwsMetricAttributesSpanProcessor produces metrics for errors (e.g.HTTP 4XX status codes), faults(e.g.HTTP 5XX status
/// codes), and latency(in Milliseconds). Errors and faults are counted, while latency is measured with a histogram.
/// Metrics are emitted with attributes derived from span attributes.
///
/// For highest fidelity metrics, this processor should be coupled with the AlwaysRecordSampler, which will result in
/// 100% of spans being sent to the processor.
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private Activity? lambdaActivity;

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
        Console.WriteLine($"Hello, this is source name = {activity.Source.Name}");

        // this processor will only be hooked in for lambda. Question would be how does this affect perf?
        // think about merging the times as well. We can merge the start but for the end, since once the lambda ends,
        // we can't guarentee that the children didn't end as well.
        if (activity.Source.Name.Equals("OpenTelemetry.Instrumentation.AWSLambda"))
        {
            this.lambdaActivity = activity;
        }

        if (activity.Source.Name.Equals("Microsoft.AspNetCore") && activity.ParentId != null && activity.ParentId.Equals(this.lambdaActivity?.SpanId))
        {
            this.lambdaActivity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// TODO: There is an OTEL discussion to add BeforeEnd to allow us to write to spans. Below is a hack and goes
    /// against the otel specs (not to edit span in OnEnd) but is required for the time being.
    /// Add BeforeEnd to have a callback where the span is still writeable open-telemetry/opentelemetry-specification#1089
    /// https://github.com/open-telemetry/opentelemetry-specification/issues/1089
    /// https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/sdk.md#onendspan
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
    }
}

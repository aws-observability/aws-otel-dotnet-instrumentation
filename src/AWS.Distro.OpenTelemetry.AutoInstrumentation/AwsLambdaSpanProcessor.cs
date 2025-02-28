// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Simple Span Processor that adds a new Lambda specific flag to the Lambda Handler Span
/// This is used to show that the lambda span belongs to a trace with multiple server spans
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private Activity? lambdaActivity;

    /// <summary>
    /// OnStart caches a reference to the lambda activity if it exists and adds
    /// a flag to signify whether there are multiple server spans or not.
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
        if (activity.Source.Name.Equals("OpenTelemetry.Instrumentation.AWSLambda"))
        {
            this.lambdaActivity = activity;
        }

        if (activity.Source.Name.Equals("Microsoft.AspNetCore") && activity.ParentId != null && 
            (activity.ParentId.Equals(this.lambdaActivity?.SpanId) || activity.ParentId.Equals(this.lambdaActivity?.Id)))
        {
            this.lambdaActivity.SetTag(AttributeAWSTraceLambdaFlagMultipleServer, "true");
        }
    }

    /// <summary>
    /// OnEnd Function
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
    }
}

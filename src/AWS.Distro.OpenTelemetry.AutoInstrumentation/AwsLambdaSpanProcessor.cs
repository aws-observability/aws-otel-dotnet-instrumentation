// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// add more summary here
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private Activity? lambdaActivity;

    /// <summary>
    /// OnStart caches a reference to the lambda activity if it exists and adds
    /// a flag to signify whether there are multipel server spans or not.
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

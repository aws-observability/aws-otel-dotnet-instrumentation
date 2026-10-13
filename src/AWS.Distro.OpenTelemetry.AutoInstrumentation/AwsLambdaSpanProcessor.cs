// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Adds Lambda span flags and requests logger provider flushes at invocation completion.
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private Activity? lambdaActivity;

    /// <summary>
    /// Gets or sets an existing processor owned by the application's logger provider.
    /// </summary>
    internal static BaseProcessor<LogRecord>? LogRecordProcessor { get; set; }

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

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        try
        {
            // AWSLambdaWrapper.OnFunctionStop flushes tracing, which invokes this processor.
            // Zero requests a batch export without waiting for completion or confirming delivery.
            (LogRecordProcessor?.ParentProvider as LoggerProvider)?.ForceFlush(0);
        }
        catch (Exception)
        {
            // Optional logging must preserve the invocation's result and tracing flush behavior.
        }

        return base.OnForceFlush(timeoutMilliseconds);
    }
}

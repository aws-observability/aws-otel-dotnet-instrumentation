// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// TODO: Add documentation here
/// </summary>
public class AwsSpanMetricsProcessor : BaseProcessor<Activity>
{
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

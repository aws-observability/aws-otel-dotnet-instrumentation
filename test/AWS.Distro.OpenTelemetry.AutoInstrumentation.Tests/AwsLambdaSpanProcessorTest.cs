// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Moq;
using OpenTelemetry;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// AwsBatchUnsampledSpanExportProcessor test class
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsLambdaSpanProcessorTest
{
    private ActivitySource lambdaActivitySource = new ActivitySource("OpenTelemetry.Instrumentation.AWSLambda");
    private ActivitySource aspNetSource = new ActivitySource("Microsoft.AspNetCore");
    private ActivitySource httpSource = new ActivitySource("Microsoft.Http");

    public AwsLambdaSpanProcessorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
    }

    /// <summary>
    /// Unit test to check if multiple server spans exists, one from lambda, and
    /// other from aspnet core and that AttributeAWSTraceLambdaFlagMultipleServer is set to true.
    /// </summary>
    [Fact]
    public void CheckThatMultipleServerSpanFlagAdded()
    {
        using var processor = new AwsLambdaSpanProcessor();

        var lambdaActivity = this.lambdaActivitySource.StartActivity("lambdaSpan", ActivityKind.Server);
        if (lambdaActivity == null)
        {
            return;
        }

        Activity.Current = lambdaActivity;

        var aspNetCoreActivity = this.aspNetSource.StartActivity("aspSpan", ActivityKind.Server);
        if (aspNetCoreActivity == null)
        {
            return;
        }

        processor.OnStart(lambdaActivity);
        processor.OnStart(aspNetCoreActivity);
        processor.Shutdown();

        Assert.Equal("true", lambdaActivity.GetTagItem(AttributeAWSTraceLambdaFlagMultipleServer));
    }

    /// <summary>
    /// Unit test to check if only lambda span exists, the flag is not added
    /// </summary>
    [Fact]
    public void CheckThatMultipleServerSpanFlagNotAdded()
    {
        using var processor = new AwsLambdaSpanProcessor();

        var lambdaActivity = this.lambdaActivitySource.StartActivity("lambdaSpan", ActivityKind.Server);
        if (lambdaActivity == null)
        {
            return;
        }

        Activity.Current = lambdaActivity;

        var httpActivity = this.aspNetSource.StartActivity("httpSpan", ActivityKind.Client);
        if (httpActivity == null)
        {
            return;
        }

        processor.OnStart(lambdaActivity);
        processor.Shutdown();

        Assert.Null(lambdaActivity.GetTagItem(AttributeAWSTraceLambdaFlagMultipleServer));
    }
}
// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Amazon.Lambda.Core;
using Microsoft.Extensions.Logging;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// AwsBatchUnsampledSpanExportProcessor test class
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
[Collection("AWS credential providers")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlushesLogsFromTheExistingLambdaStopPath(bool sampled)
    {
        using var logProcessor = new FlushRecordingProcessor();
        var originalProcessor = AwsLambdaSpanProcessor.LogRecordProcessor;
        AwsLambdaSpanProcessor.LogRecordProcessor = logProcessor;

        try
        {
            using var tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAWSLambdaConfigurations()
                .SetSampler(sampled ? new AlwaysOnSampler() : new AlwaysOffSampler())
                .AddProcessor(new AwsLambdaSpanProcessor())
                .Build();
            using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options => options.AddProcessor(logProcessor)));
            var context = Mock.Of<ILambdaContext>(value =>
                value.FunctionName == "test-function" &&
                value.InvokedFunctionArn == "arn:aws:lambda:us-east-1:123456789012:function:test-function");
            const string expectedResult = "handler result";

            // A first invocation without logging must not construct a logger provider.
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            Assert.Null(logProcessor.ParentProvider);
            Assert.Equal(0, logProcessor.FlushCount);

            // Logging can initialize during the handler, before the existing OnFunctionStop runs.
            var actualResult = await AWSLambdaWrapper.TraceAsync(
                tracerProvider,
                async (_, _) =>
                {
                    factory.CreateLogger("Lambda.Flush.Tests").LogInformation("last invocation log");
                    await Task.Yield();
                    return expectedResult;
                },
                "input",
                context);
            Assert.Equal(expectedResult, actualResult);
            Assert.Equal(1, logProcessor.FlushCount);
            Assert.Equal(0, logProcessor.TimeoutMilliseconds);

            Assert.Equal(expectedResult, AWSLambdaWrapper.Trace(
                tracerProvider, (_, _) => expectedResult, "input", context));
            Assert.Equal(2, logProcessor.FlushCount);

            // A false logger flush result must not fail the original tracing flush.
            Assert.True(tracerProvider.ForceFlush());
            Assert.Equal(0, logProcessor.TimeoutMilliseconds);
            logProcessor.ThrowOnFlush = true;
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            var expectedException = new InvalidOperationException("handler failed");
            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(() => AWSLambdaWrapper.TraceAsync(
                tracerProvider, (_, _) => Task.FromException<string>(expectedException), "input", context));
            Assert.Same(expectedException, actualException);
            Assert.Equal(5, logProcessor.FlushCount);
            Assert.Equal(0, logProcessor.TimeoutMilliseconds);

            factory.Dispose();
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            Assert.Equal(5, logProcessor.FlushCount);

            AwsLambdaSpanProcessor.LogRecordProcessor = null;
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
        }
        finally
        {
            AwsLambdaSpanProcessor.LogRecordProcessor = originalProcessor;
        }
    }

    private sealed class FlushRecordingProcessor : BaseProcessor<LogRecord>
    {
        public bool ThrowOnFlush { get; set; }

        public int FlushCount { get; private set; }

        public int TimeoutMilliseconds { get; private set; }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            this.FlushCount++;
            this.TimeoutMilliseconds = timeoutMilliseconds;
            return this.ThrowOnFlush ? throw new InvalidOperationException("flush failed") : false;
        }
    }
}

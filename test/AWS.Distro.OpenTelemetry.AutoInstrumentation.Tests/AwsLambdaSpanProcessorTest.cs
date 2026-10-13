// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Amazon.Lambda.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
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
        var logProcessorMock = new Mock<BaseProcessor<LogRecord>> { CallBase = true };
        logProcessorMock.Protected().Setup<bool>("OnForceFlush", ItExpr.IsAny<int>()).Returns(false);
        using var logProcessor = logProcessorMock.Object;
        using var noOpProcessor = new AwsLambdaSpanProcessor.NoOpLogRecordProcessor();
        var originalProcessor = AwsLambdaSpanProcessor.LogRecordProcessor;
        AwsLambdaSpanProcessor.LogRecordProcessor = noOpProcessor;

        try
        {
            using var tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAWSLambdaConfigurations()
                .SetSampler(sampled ? new AlwaysOnSampler() : new AlwaysOffSampler())
                .AddProcessor(new AwsLambdaSpanProcessor())
                .Build();
            using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options => options
                .AddProcessor(noOpProcessor)
                .AddProcessor(logProcessor)));
            var context = Mock.Of<ILambdaContext>(value =>
                value.FunctionName == "test-function" &&
                value.InvokedFunctionArn == "arn:aws:lambda:us-east-1:123456789012:function:test-function");
            const string expectedResult = "handler result";

            // A first invocation without logging must not construct a logger provider.
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            Assert.Null(logProcessor.ParentProvider);
            Assert.Null(AwsLambdaSpanProcessor.LoggerProvider);
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Never(), ItExpr.IsAny<int>());

            // Logging can initialize during the handler, before the existing OnFunctionStop runs.
            var actualResult = await AWSLambdaWrapper.TraceAsync(
                tracerProvider,
                async (_, _) =>
                {
                    var logger = factory.CreateLogger("Lambda.Flush.Tests");
                    Assert.IsAssignableFrom<LoggerProvider>(AwsLambdaSpanProcessor.LoggerProvider);
                    Assert.Same(logProcessor.ParentProvider, AwsLambdaSpanProcessor.LoggerProvider);
                    logger.LogInformation("last invocation log");
                    await Task.Yield();
                    return expectedResult;
                },
                "input",
                context);
            Assert.Equal(expectedResult, actualResult);
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Once(), ItExpr.Is<int>(timeout => timeout == 0));

            Assert.Equal(expectedResult, AWSLambdaWrapper.Trace(
                tracerProvider, (_, _) => expectedResult, "input", context));
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Exactly(2), ItExpr.Is<int>(timeout => timeout == 0));

            // A false logger flush result must not fail the original tracing flush.
            Assert.True(tracerProvider.ForceFlush());
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Exactly(3), ItExpr.Is<int>(timeout => timeout == 0));
            logProcessorMock.Protected().Setup<bool>("OnForceFlush", ItExpr.IsAny<int>())
                .Throws(new InvalidOperationException("flush failed"));
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            var expectedException = new InvalidOperationException("handler failed");
            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(() => AWSLambdaWrapper.TraceAsync(
                tracerProvider, (_, _) => Task.FromException<string>(expectedException), "input", context));
            Assert.Same(expectedException, actualException);
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Exactly(5), ItExpr.Is<int>(timeout => timeout == 0));

            factory.Dispose();
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Exactly(5), ItExpr.Is<int>(timeout => timeout == 0));

            AwsLambdaSpanProcessor.LogRecordProcessor = null;
            Assert.Equal(expectedResult, await AWSLambdaWrapper.TraceAsync(tracerProvider, (_, _) => Task.FromResult(expectedResult), "input", context));
            logProcessorMock.Protected().Verify("OnForceFlush", Times.Exactly(5), ItExpr.Is<int>(timeout => timeout == 0));
        }
        finally
        {
            AwsLambdaSpanProcessor.LogRecordProcessor = originalProcessor;
        }
    }
}

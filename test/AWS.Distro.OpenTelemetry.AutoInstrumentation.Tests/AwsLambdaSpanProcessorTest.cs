// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldWaitForBatchLogExportAtLambdaInvocationCompletion(bool sampled, bool handlerThrows)
    {
        using var exporter = new BlockingLogExporter();
        using var factory = CreateLoggerFactory(new BatchLogRecordExportProcessor(
            exporter, scheduledDelayMilliseconds: 60000));
        using var provider = CreateTracerProvider(sampled);
        var logger = factory.CreateLogger("Lambda.Flush.Tests");
        var expectedException = new InvalidOperationException("handler failed");

        var invocation = Task.Run(() => AWSLambdaWrapper.TraceAsync(
            provider,
            (_, _) =>
            {
                logger.LogInformation("last invocation log");
                return handlerThrows
                    ? Task.FromException<string>(expectedException)
                    : Task.FromResult("handler result");
            },
            "input",
            CreateLambdaContext()));

        try
        {
            Assert.True(exporter.ExportStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(invocation.IsCompleted);
            Assert.Empty(exporter.Messages);
            exporter.AllowExport.Set();

            if (handlerThrows)
            {
                var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => invocation.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(expectedException, actualException);
            }
            else
            {
                Assert.Equal("handler result", await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal("last invocation log", Assert.Single(exporter.Messages));
        }
        finally
        {
            exporter.AllowExport.Set();
        }
    }

    [Fact]
    public void ShouldFlushEachLoggerProviderAndStopTrackingDisposedProviders()
    {
        var firstProcessor = new Mock<BaseProcessor<LogRecord>> { CallBase = true };
        firstProcessor.Protected().Setup<bool>("OnForceFlush", ItExpr.IsAny<int>()).Returns(true);
        var secondProcessor = new Mock<BaseProcessor<LogRecord>> { CallBase = true };
        secondProcessor.Protected().Setup<bool>("OnForceFlush", ItExpr.IsAny<int>()).Returns(true);
        using var firstFactory = CreateLoggerFactory(firstProcessor.Object);
        using var secondFactory = CreateLoggerFactory(secondProcessor.Object);
        using var provider = CreateTracerProvider(sampled: true);
        firstFactory.CreateLogger("First").LogInformation("first");
        secondFactory.CreateLogger("Second").LogInformation("second");

        Assert.Equal("result", AWSLambdaWrapper.Trace(provider, (_, _) => "result", "input", CreateLambdaContext()));
        firstProcessor.Protected().Verify("OnForceFlush", Times.Once(), ItExpr.Is<int>(timeout => timeout > 0 && timeout <= 5000));
        secondProcessor.Protected().Verify("OnForceFlush", Times.Once(), ItExpr.Is<int>(timeout => timeout > 0 && timeout <= 5000));

        firstFactory.Dispose();
        Assert.True(provider.ForceFlush(1000));
        firstProcessor.Protected().Verify("OnForceFlush", Times.Once(), ItExpr.IsAny<int>());
        secondProcessor.Protected().Verify("OnForceFlush", Times.Exactly(2), ItExpr.Is<int>(timeout => timeout > 0 && timeout <= 5000));
        secondProcessor.Protected().Verify("OnForceFlush", Times.Once(), ItExpr.Is<int>(timeout => timeout > 0 && timeout <= 1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveTheHandlerResultAndExceptionWhenLogFlushingFails(bool flushThrows)
    {
        var processor = new Mock<BaseProcessor<LogRecord>> { CallBase = true };
        var flush = processor.Protected().Setup<bool>("OnForceFlush", ItExpr.IsAny<int>());
        if (flushThrows)
        {
            flush.Throws(new InvalidOperationException("export failed"));
        }
        else
        {
            flush.Returns(false);
        }

        using var factory = CreateLoggerFactory(processor.Object);
        using var provider = CreateTracerProvider(sampled: false);
        factory.CreateLogger("Lambda.Flush.Tests").LogInformation("test log");

        var successInvocation = AWSLambdaWrapper.TraceAsync(provider, (_, _) => Task.FromResult("result"), "input", CreateLambdaContext());
        Assert.Equal("result", await successInvocation);
        var expectedException = new InvalidOperationException("handler failed");
        var failedInvocation = AWSLambdaWrapper.TraceAsync(provider, (_, _) => Task.FromException<string>(expectedException), "input", CreateLambdaContext());
        Assert.Same(expectedException, await Assert.ThrowsAsync<InvalidOperationException>(() => failedInvocation));
        Assert.True(provider.ForceFlush());
    }

    private static ILoggerFactory CreateLoggerFactory(BaseProcessor<LogRecord> processor)
        => LoggerFactory.Create(builder => builder.AddOpenTelemetry(options => options
            .AddProcessor(new AwsLambdaSpanProcessor.LogRecordProcessor())
            .AddProcessor(processor)));

    private static TracerProvider CreateTracerProvider(bool sampled)
        => Sdk.CreateTracerProviderBuilder()
            .AddAWSLambdaConfigurations()
            .SetSampler(sampled ? new AlwaysOnSampler() : new AlwaysOffSampler())
            .AddProcessor(new AwsLambdaSpanProcessor())
            .Build();

    private static ILambdaContext CreateLambdaContext()
        => Mock.Of<ILambdaContext>(context =>
            context.FunctionName == "test-function" &&
            context.InvokedFunctionArn == "arn:aws:lambda:us-east-1:123456789012:function:test-function");

    private sealed class BlockingLogExporter : BaseExporter<LogRecord>
    {
        public ManualResetEventSlim ExportStarted { get; } = new();

        public ManualResetEventSlim AllowExport { get; } = new();

        public ConcurrentQueue<string?> Messages { get; } = new();

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            this.ExportStarted.Set();
            if (!this.AllowExport.Wait(TimeSpan.FromSeconds(5)))
            {
                return ExportResult.Failure;
            }

            foreach (var record in batch)
            {
                this.Messages.Enqueue(record.Body);
            }

            return ExportResult.Success;
        }
    }
}

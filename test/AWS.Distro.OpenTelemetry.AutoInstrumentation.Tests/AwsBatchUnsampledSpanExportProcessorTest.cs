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
public class AwsBatchUnsampledSpanExportProcessorTest
{
    /// <summary>
    /// Unit test to check sampled activity is processed
    /// and that AttributeAWSTraceFlagSampled is not set at all.
    /// </summary>
    [Fact]
    public void CheckThatSampledActivityIsNotProcessed()
    {
        var exporter = new Mock<BaseExporter<Activity>>();
        using var processor = new AwsBatchUnsampledSpanExportProcessor(
            exporter.Object,
            maxQueueSize: 1,
            maxExportBatchSize: 1);

        using var activity = new Activity("start")
        {
            ActivityTraceFlags = ActivityTraceFlags.Recorded,
        };

        processor.OnEnd(activity);
        processor.Shutdown();

        Assert.Null(activity.GetTagItem(AttributeAWSTraceFlagSampled));

        PropertyInfo? processedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("ProcessedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var processedCount = processedCountInfo?.GetValue(processor);
        if (processedCount != null)
        {
            Assert.Equal(0, (long)processedCount);
        }

        PropertyInfo? receivedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("ReceivedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var receivedCount = receivedCountInfo?.GetValue(processor);
        if (receivedCount != null)
        {
            Assert.Equal(0, (long)receivedCount);
        }

        PropertyInfo? droppedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("DroppedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var droppedCount = droppedCountInfo?.GetValue(processor);
        if (droppedCount != null)
        {
            Assert.Equal(0, (long)droppedCount);
        }
    }

    /// <summary>
    /// Unit test to check unsampled activity is processed and not dropped
    /// and that AttributeAWSTraceFlagSampled is also set to false.
    /// </summary>
    [Fact]
    public void CheckThatUnSampledActivityIsProcessed()
    {
        var exporter = new Mock<BaseExporter<Activity>>();
        using var processor = new AwsBatchUnsampledSpanExportProcessor(
            exporter.Object,
            maxQueueSize: 1,
            maxExportBatchSize: 1);

        using var activity = new Activity("start")
        {
            ActivityTraceFlags = ActivityTraceFlags.None,
        };

        processor.OnEnd(activity);
        processor.Shutdown();

        Assert.Equal("false", activity.GetTagItem(AttributeAWSTraceFlagSampled));

        PropertyInfo? processedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("ProcessedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var processedCount = processedCountInfo?.GetValue(processor);
        if (processedCount != null)
        {
            Assert.Equal(1, (long)processedCount);
        }

        PropertyInfo? receivedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("ReceivedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var receivedCount = receivedCountInfo?.GetValue(processor);
        if (receivedCount != null)
        {
            Assert.Equal(1, (long)receivedCount);
        }

        PropertyInfo? droppedCountInfo = typeof(BatchExportProcessor<Activity>).GetProperty("DroppedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        var droppedCount = droppedCountInfo?.GetValue(processor);
        if (droppedCount != null)
        {
            Assert.Equal(0, (long)droppedCount);
        }
    }
}
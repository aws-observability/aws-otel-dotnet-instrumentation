// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics;
using FluentAssertions;
using Moq;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class CloudWatchLogsClientTest
{
    private readonly Mock<IAmazonCloudWatchLogs> mockLogsClient;
    private readonly CloudWatchLogsClient logClient;

    public CloudWatchLogsClientTest()
    {
        mockLogsClient = new Mock<IAmazonCloudWatchLogs>();
        
        // Setup default mock responses
        mockLogsClient.Setup(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default))
            .ReturnsAsync(new CreateLogGroupResponse());
        mockLogsClient.Setup(x => x.CreateLogStreamAsync(It.IsAny<CreateLogStreamRequest>(), default))
            .ReturnsAsync(new CreateLogStreamResponse());
        mockLogsClient.Setup(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default))
            .ReturnsAsync(new PutLogEventsResponse { NextSequenceToken = "12345" });

        // Create client with mocked AWS client using reflection
        logClient = new CloudWatchLogsClient("test-log-group");
        var logsClientField = typeof(CloudWatchLogsClient).GetField("_logsClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        logsClientField?.SetValue(logClient, mockLogsClient.Object);
    }

    [Fact]
    public void TestInitialization()
    {
        var logGroupField = typeof(CloudWatchLogsClient).GetField("_logGroupName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var logStreamField = typeof(CloudWatchLogsClient).GetField("_logStreamName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        logGroupField?.GetValue(logClient).Should().Be("test-log-group");
        var logStreamName = logStreamField?.GetValue(logClient) as string;
        logStreamName.Should().NotBeNullOrEmpty();
        logStreamName.Should().StartWith("otel-dotnet-");
    }

    [Fact]
    public void TestInitializationWithCustomParams()
    {
        var customClient = new CloudWatchLogsClient("custom-log-group", "custom-stream");
        
        var logGroupField = typeof(CloudWatchLogsClient).GetField("_logGroupName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var logStreamField = typeof(CloudWatchLogsClient).GetField("_logStreamName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        logGroupField?.GetValue(customClient).Should().Be("custom-log-group");
        logStreamField?.GetValue(customClient).Should().Be("custom-stream");
    }

    [Fact]
    public void TestGenerateLogStreamName()
    {
        var generateMethod = typeof(CloudWatchLogsClient).GetMethod("GenerateLogStreamName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var name1 = generateMethod?.Invoke(null, null) as string;
        var name2 = generateMethod?.Invoke(null, null) as string;

        name1.Should().NotBe(name2);
        name1.Should().StartWith("otel-dotnet-");
        name2.Should().StartWith("otel-dotnet-");
        name1.Should().HaveLength("otel-dotnet-".Length + 8);
    }

    [Fact]
    public async Task TestEnsureLogGroupExists()
    {
        var ensureMethod = typeof(CloudWatchLogsClient).GetMethod("EnsureLogGroupExistsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        await (Task)ensureMethod?.Invoke(logClient, null)!;
        
        mockLogsClient.Verify(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task TestEnsureLogGroupExistsAlreadyExists()
    {
        mockLogsClient.Setup(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default))
            .ThrowsAsync(new ResourceAlreadyExistsException("Already exists"));

        var ensureMethod = typeof(CloudWatchLogsClient).GetMethod("EnsureLogGroupExistsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        await (Task)ensureMethod?.Invoke(logClient, null)!;
        
        mockLogsClient.Verify(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task TestEnsureLogGroupExistsFailure()
    {
        mockLogsClient.Setup(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default))
            .ThrowsAsync(new AmazonCloudWatchLogsException("Access denied"));

        var ensureMethod = typeof(CloudWatchLogsClient).GetMethod("EnsureLogGroupExistsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        await Assert.ThrowsAsync<AmazonCloudWatchLogsException>(async () => 
            await (Task)ensureMethod?.Invoke(logClient, null)!);
    }

    [Fact]
    public void TestCreateEventBatch()
    {
        var createMethod = typeof(CloudWatchLogsClient).GetMethod("CreateEventBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = createMethod?.Invoke(null, null) as LogEventBatch;

        batch.Should().NotBeNull();
        batch!.LogEvents.Should().BeEmpty();
        batch.ByteTotal.Should().Be(0);
        batch.MinTimestampMs.Should().Be(0);
        batch.MaxTimestampMs.Should().Be(0);
        batch.CreatedTimestampMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TestValidateLogEventValid()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var logEvent = new InputLogEvent
        {
            Message = "test message",
            Timestamp = DateTime.UtcNow
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeTrue();
    }

    [Fact]
    public void TestValidateLogEventEmptyMessage()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var logEvent = new InputLogEvent
        {
            Message = "",
            Timestamp = DateTime.UtcNow
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestValidateLogEventWhitespaceMessage()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var logEvent = new InputLogEvent
        {
            Message = "   ",
            Timestamp = DateTime.UtcNow
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestValidateLogEventOversizedMessage()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var largeMessage = new string('x', CloudWatchLogsClient.CwMaxEventPayloadBytes + 100);
        var logEvent = new InputLogEvent
        {
            Message = largeMessage,
            Timestamp = DateTime.UtcNow
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeTrue();
        logEvent.Message.Length.Should().BeLessThan(largeMessage.Length);
        logEvent.Message.Should().EndWith(CloudWatchLogsClient.CwTruncatedSuffix);
    }

    [Fact]
    public void TestValidateLogEventOldTimestamp()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var oldTimestamp = DateTime.UtcNow.AddDays(-15);
        var logEvent = new InputLogEvent
        {
            Message = "test message",
            Timestamp = oldTimestamp
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestValidateLogEventFutureTimestamp()
    {
        var validateMethod = typeof(CloudWatchLogsClient).GetMethod("ValidateLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var futureTimestamp = DateTime.UtcNow.AddHours(3);
        var logEvent = new InputLogEvent
        {
            Message = "test message",
            Timestamp = futureTimestamp
        };

        var result = (bool)validateMethod?.Invoke(logClient, new object[] { logEvent })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestEventBatchExceedsLimitByCount()
    {
        var exceedsMethod = typeof(CloudWatchLogsClient).GetMethod("EventBatchExceedsLimit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        for (int i = 0; i < CloudWatchLogsClient.CwMaxRequestEventCount; i++)
        {
            batch.LogEvents.Add(new InputLogEvent { Message = "test" });
        }

        var result = (bool)exceedsMethod?.Invoke(null, new object[] { batch, 100 })!;
        result.Should().BeTrue();
    }

    [Fact]
    public void TestEventBatchExceedsLimitBySize()
    {
        var exceedsMethod = typeof(CloudWatchLogsClient).GetMethod("EventBatchExceedsLimit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        batch.ByteTotal = CloudWatchLogsClient.CwMaxRequestPayloadBytes - 50;

        var result = (bool)exceedsMethod?.Invoke(null, new object[] { batch, 100 })!;
        result.Should().BeTrue();
    }

    [Fact]
    public void TestEventBatchWithinLimits()
    {
        var exceedsMethod = typeof(CloudWatchLogsClient).GetMethod("EventBatchExceedsLimit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        for (int i = 0; i < 10; i++)
        {
            batch.LogEvents.Add(new InputLogEvent { Message = "test" });
        }
        batch.ByteTotal = 1000;

        var result = (bool)exceedsMethod?.Invoke(null, new object[] { batch, 100 })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestIsBatchActiveNewBatch()
    {
        var isActiveMethod = typeof(CloudWatchLogsClient).GetMethod("IsBatchActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = (bool)isActiveMethod?.Invoke(null, new object[] { batch, currentTime })!;
        result.Should().BeTrue();
    }

    [Fact]
    public void TestIsBatchActive24HourSpan()
    {
        var isActiveMethod = typeof(CloudWatchLogsClient).GetMethod("IsBatchActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        batch.MinTimestampMs = currentTime;
        batch.MaxTimestampMs = currentTime;

        var futureTimestamp = currentTime + (25 * 60 * 60 * 1000);

        var result = (bool)isActiveMethod?.Invoke(null, new object[] { batch, futureTimestamp })!;
        result.Should().BeFalse();
    }

    [Fact]
    public void TestAppendToBatch()
    {
        var batch = new LogEventBatch();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var logEvent = new InputLogEvent
        {
            Message = "test message",
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(currentTime).UtcDateTime
        };
        var eventSize = 100;

        batch.AddEvent(logEvent, eventSize);

        batch.LogEvents.Count.Should().Be(1);
        batch.ByteTotal.Should().Be(eventSize);
        batch.MinTimestampMs.Should().Be(currentTime);
        batch.MaxTimestampMs.Should().Be(currentTime);
    }

    [Fact]
    public void TestSortLogEvents()
    {
        var sortMethod = typeof(CloudWatchLogsClient).GetMethod("SortLogEvents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var batch = new LogEventBatch();
        var currentTime = DateTime.UtcNow;

        var events = new[]
        {
            new InputLogEvent { Message = "third", Timestamp = currentTime.AddSeconds(2) },
            new InputLogEvent { Message = "first", Timestamp = currentTime },
            new InputLogEvent { Message = "second", Timestamp = currentTime.AddSeconds(1) }
        };

        batch.LogEvents.AddRange(events);
        sortMethod?.Invoke(null, new object[] { batch });

        batch.LogEvents[0].Message.Should().Be("first");
        batch.LogEvents[1].Message.Should().Be("second");
        batch.LogEvents[2].Message.Should().Be("third");
    }

    [Fact]
    public async Task TestFlushPendingEvents()
    {
        var eventBatchField = typeof(CloudWatchLogsClient).GetField("_eventBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var batch = new LogEventBatch();
        batch.AddEvent(new InputLogEvent { Message = "test", Timestamp = DateTime.UtcNow }, 10);
        eventBatchField?.SetValue(logClient, batch);

        await logClient.FlushPendingEventsAsync();

        mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task TestFlushPendingEventsNoPendingEvents()
    {
        var eventBatchField = typeof(CloudWatchLogsClient).GetField("_eventBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        eventBatchField?.SetValue(logClient, null);

        await logClient.FlushPendingEventsAsync();

        mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task TestSendLogEvent()
    {
        var logEvent = new AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics.LogEvent { Message = "test message", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        await this.logClient.SendLogEventAsync(logEvent);

        var eventBatchField = typeof(CloudWatchLogsClient).GetField("_eventBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var batch = eventBatchField?.GetValue(this.logClient) as LogEventBatch;
        
        batch.Should().NotBeNull();
        batch!.Size().Should().Be(1);
        
        // Verify the mock was not called since we're just batching
        this.mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task TestSendLogBatchWithResourceNotFound()
    {
        var sendBatchMethod = typeof(CloudWatchLogsClient).GetMethod("SendLogBatchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var batch = new LogEventBatch();
        batch.AddEvent(new InputLogEvent { Message = "test message", Timestamp = DateTime.UtcNow }, 10);

        mockLogsClient.SetupSequence(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default))
            .ThrowsAsync(new ResourceNotFoundException("Not found"))
            .ReturnsAsync(new PutLogEventsResponse { NextSequenceToken = "12345" });

        var result = await (Task<PutLogEventsResponse?>)sendBatchMethod?.Invoke(logClient, new object[] { batch })!;

        result.Should().NotBeNull();
        result!.NextSequenceToken.Should().Be("12345");
        mockLogsClient.Verify(x => x.CreateLogGroupAsync(It.IsAny<CreateLogGroupRequest>(), default), Times.Once);
        mockLogsClient.Verify(x => x.CreateLogStreamAsync(It.IsAny<CreateLogStreamRequest>(), default), Times.Once);
        mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Exactly(2));
    }

    [Fact]
    public async Task TestSendLogBatchEmptyBatch()
    {
        var sendBatchMethod = typeof(CloudWatchLogsClient).GetMethod("SendLogBatchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var batch = new LogEventBatch();

        var result = await (Task<PutLogEventsResponse?>)sendBatchMethod?.Invoke(logClient, new object[] { batch })!;

        result.Should().BeNull();
        mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task TestSendLogEventWithInvalidEvent()
    {
        var logEvent = new AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics.LogEvent { Message = string.Empty, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        await logClient.SendLogEventAsync(logEvent);

        mockLogsClient.Verify(x => x.PutLogEventsAsync(It.IsAny<PutLogEventsRequest>(), default), Times.Never);
    }

    [Fact]
    public void TestLogEventBatchClear()
    {
        var batch = new LogEventBatch();
        batch.AddEvent(new InputLogEvent { Message = "test", Timestamp = DateTime.UtcNow }, 100);

        batch.IsEmpty().Should().BeFalse();
        batch.Size().Should().Be(1);

        batch.Clear();
        batch.IsEmpty().Should().BeTrue();
        batch.Size().Should().Be(0);
        batch.ByteTotal.Should().Be(0);
    }

    [Fact]
    public void TestLogEventBatchTimestampTracking()
    {
        var batch = new LogEventBatch();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        batch.AddEvent(new InputLogEvent { Message = "first", Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(currentTime).UtcDateTime }, 10);
        batch.MinTimestampMs.Should().Be(currentTime);
        batch.MaxTimestampMs.Should().Be(currentTime);

        var earlierTime = currentTime - 1000;
        batch.AddEvent(new InputLogEvent { Message = "earlier", Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(earlierTime).UtcDateTime }, 10);
        batch.MinTimestampMs.Should().Be(earlierTime);
        batch.MaxTimestampMs.Should().Be(currentTime);

        var laterTime = currentTime + 1000;
        batch.AddEvent(new InputLogEvent { Message = "later", Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(laterTime).UtcDateTime }, 10);
        batch.MinTimestampMs.Should().Be(earlierTime);
        batch.MaxTimestampMs.Should().Be(laterTime);
    }
}
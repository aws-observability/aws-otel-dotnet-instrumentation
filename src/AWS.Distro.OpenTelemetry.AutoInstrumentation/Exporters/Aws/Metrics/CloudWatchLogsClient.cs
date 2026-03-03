// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics
{
    /// <summary>
    /// Container for a batch of CloudWatch log events with metadata.
    /// </summary>
    public class LogEventBatch
    {
        public List<InputLogEvent> LogEvents { get; } = new();
        public int ByteTotal { get; set; }
        public long MinTimestampMs { get; set; }
        public long MaxTimestampMs { get; set; }
        public long CreatedTimestampMs { get; }

        public LogEventBatch()
        {
            CreatedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Add a log event to the batch.
        /// </summary>
        public void AddEvent(InputLogEvent logEvent, int eventSize)
        {
            LogEvents.Add(logEvent);
            ByteTotal += eventSize;

            var timestampMs = ((DateTimeOffset)logEvent.Timestamp).ToUnixTimeMilliseconds();
            if (MinTimestampMs == 0 || timestampMs < MinTimestampMs)
            {
                MinTimestampMs = timestampMs;
            }
            if (timestampMs > MaxTimestampMs)
            {
                MaxTimestampMs = timestampMs;
            }
        }

        /// <summary>
        /// Check if the batch is empty.
        /// </summary>
        public bool IsEmpty() => LogEvents.Count == 0;

        /// <summary>
        /// Get the number of events in the batch.
        /// </summary>
        public int Size() => LogEvents.Count;

        /// <summary>
        /// Clear the batch.
        /// </summary>
        public void Clear()
        {
            LogEvents.Clear();
            ByteTotal = 0;
            MinTimestampMs = 0;
            MaxTimestampMs = 0;
        }
    }

    /// <summary>
    /// CloudWatch Logs client for batching and sending log events.
    /// </summary>
    public class CloudWatchLogsClient
    {
        // CloudWatch Logs limits
        public const int CwMaxEventPayloadBytes = 256 * 1024; // 256KB
        public const int CwMaxRequestEventCount = 10000;
        public const int CwPerEventHeaderBytes = 26;
        public const int BatchFlushInterval = 60 * 1000; // 60 seconds
        public const int CwMaxRequestPayloadBytes = 1 * 1024 * 1024; // 1MB
        public const string CwTruncatedSuffix = "[Truncated...]";
        public const long CwEventTimestampLimitPast = 14 * 24 * 60 * 60 * 1000; // 14 days
        public const long CwEventTimestampLimitFuture = 2 * 60 * 60 * 1000; // 2 hours

        private readonly string _logGroupName;
        private readonly string _logStreamName;
        private readonly IAmazonCloudWatchLogs _logsClient;
        private LogEventBatch? _eventBatch;

        public CloudWatchLogsClient(string logGroupName, string? logStreamName = null, AmazonCloudWatchLogsConfig? config = null)
        {
            _logGroupName = logGroupName;
            _logStreamName = logStreamName ?? GenerateLogStreamName();
            _logsClient = new AmazonCloudWatchLogsClient(config ?? new AmazonCloudWatchLogsConfig());
        }

        /// <summary>
        /// Generate a unique log stream name.
        /// </summary>
        private static string GenerateLogStreamName()
        {
            var uniqueId = Guid.NewGuid().ToString("N")[..8];
            return $"otel-dotnet-{uniqueId}";
        }

        /// <summary>
        /// Ensure the log group exists, create if it doesn't.
        /// </summary>
        private async Task EnsureLogGroupExistsAsync()
        {
            try
            {
                await _logsClient.CreateLogGroupAsync(new CreateLogGroupRequest
                {
                    LogGroupName = _logGroupName
                });
            }
            catch (ResourceAlreadyExistsException)
            {
                // Log group already exists, which is fine
            }
        }

        /// <summary>
        /// Ensure the log stream exists, create if it doesn't.
        /// </summary>
        private async Task EnsureLogStreamExistsAsync()
        {
            try
            {
                await _logsClient.CreateLogStreamAsync(new CreateLogStreamRequest
                {
                    LogGroupName = _logGroupName,
                    LogStreamName = _logStreamName
                });
            }
            catch (ResourceAlreadyExistsException)
            {
                // Log stream already exists, which is fine
            }
        }

        /// <summary>
        /// Validate the log event according to CloudWatch Logs constraints.
        /// </summary>
        private bool ValidateLogEvent(InputLogEvent logEvent)
        {
            if (string.IsNullOrWhiteSpace(logEvent.Message))
            {
                return false;
            }

            // Check message size
            var messageSize = logEvent.Message.Length + CwPerEventHeaderBytes;
            if (messageSize > CwMaxEventPayloadBytes)
            {
                var maxMessageSize = CwMaxEventPayloadBytes - CwPerEventHeaderBytes - CwTruncatedSuffix.Length;
                logEvent.Message = logEvent.Message[..maxMessageSize] + CwTruncatedSuffix;
            }

            // Check timestamp constraints
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var logEventTimestampMs = ((DateTimeOffset)logEvent.Timestamp).ToUnixTimeMilliseconds();
            var timeDiff = currentTime - logEventTimestampMs;

            if (timeDiff > CwEventTimestampLimitPast || timeDiff < -CwEventTimestampLimitFuture)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Create a new log event batch.
        /// </summary>
        private static LogEventBatch CreateEventBatch() => new();

        /// <summary>
        /// Check if adding the next event would exceed CloudWatch Logs limits.
        /// </summary>
        private static bool EventBatchExceedsLimit(LogEventBatch batch, int nextEventSize)
        {
            return batch.Size() >= CwMaxRequestEventCount ||
                   batch.ByteTotal + nextEventSize > CwMaxRequestPayloadBytes;
        }

        /// <summary>
        /// Check if the event batch is active and can accept the event.
        /// </summary>
        private static bool IsBatchActive(LogEventBatch batch, long targetTimestampMs)
        {
            // New log event batch
            if (batch.MinTimestampMs == 0 || batch.MaxTimestampMs == 0)
            {
                return true;
            }

            // Check if adding the event would make the batch span more than 24 hours
            if (targetTimestampMs - batch.MinTimestampMs > 24 * 3600 * 1000 ||
                batch.MaxTimestampMs - targetTimestampMs > 24 * 3600 * 1000)
            {
                return false;
            }

            // Flush the event batch when reached 60s interval
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return currentTime - batch.CreatedTimestampMs < BatchFlushInterval;
        }

        /// <summary>
        /// Sort log events in the batch by timestamp.
        /// </summary>
        private static void SortLogEvents(LogEventBatch batch)
        {
            batch.LogEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }

        /// <summary>
        /// Send a batch of log events to CloudWatch Logs.
        /// </summary>
        private async Task<PutLogEventsResponse?> SendLogBatchAsync(LogEventBatch batch)
        {
            if (batch.IsEmpty())
            {
                return null;
            }

            SortLogEvents(batch);

            var request = new PutLogEventsRequest
            {
                LogGroupName = _logGroupName,
                LogStreamName = _logStreamName,
                LogEvents = batch.LogEvents
            };

            try
            {
                return await _logsClient.PutLogEventsAsync(request);
            }
            catch (ResourceNotFoundException)
            {
                // Create log group and stream, then retry
                await EnsureLogGroupExistsAsync();
                await EnsureLogStreamExistsAsync();
                return await _logsClient.PutLogEventsAsync(request);
            }
        }

        /// <summary>
        /// Send a log event to CloudWatch Logs.
        /// </summary>
        public async Task SendLogEventAsync(LogEvent logEvent)
        {
            var inputLogEvent = new InputLogEvent
            {
                Message = logEvent.Message,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.Timestamp).UtcDateTime
            };

            if (!ValidateLogEvent(inputLogEvent))
            {
                return;
            }

            var eventSize = inputLogEvent.Message.Length + CwPerEventHeaderBytes;

            // Initialize event batch if needed
            _eventBatch ??= CreateEventBatch();

            // Check if we need to send the current batch and create a new one
            var currentBatch = _eventBatch;
            var inputLogEventTimestampMs = ((DateTimeOffset)inputLogEvent.Timestamp).ToUnixTimeMilliseconds();
            if (EventBatchExceedsLimit(currentBatch, eventSize) ||
                !IsBatchActive(currentBatch, inputLogEventTimestampMs))
            {
                await SendLogBatchAsync(currentBatch);
                _eventBatch = CreateEventBatch();
                currentBatch = _eventBatch;
            }

            // Add the log event to the batch
            currentBatch.AddEvent(inputLogEvent, eventSize);
        }

        /// <summary>
        /// Force flush any pending log events.
        /// </summary>
        public async Task FlushPendingEventsAsync()
        {
            if (_eventBatch != null && !_eventBatch.IsEmpty())
            {
                var currentBatch = _eventBatch;
                _eventBatch = CreateEventBatch();
                await SendLogBatchAsync(currentBatch);
            }
        }
    }
}
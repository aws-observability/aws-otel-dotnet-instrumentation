// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Amazon.CloudWatchLogs;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics
{
    /// <summary>
    /// OpenTelemetry metrics exporter for CloudWatch EMF format.
    /// 
    /// This exporter converts OTel metrics into CloudWatch EMF logs which are then
    /// sent to CloudWatch Logs. CloudWatch Logs automatically extracts the metrics
    /// from the EMF logs.
    /// </summary>
    public class AwsCloudWatchEmfExporter : EmfExporterBase
    {
        private readonly CloudWatchLogsClient _logClient;

        public AwsCloudWatchEmfExporter(
            string namespaceName = "default",
            string logGroupName = "aws/otel/metrics",
            string? logStreamName = null,
            AmazonCloudWatchLogsConfig? cloudWatchLogsConfig = null)
            : base(namespaceName)
        {
            _logClient = new CloudWatchLogsClient(logGroupName, logStreamName, cloudWatchLogsConfig);
        }

        /// <summary>
        /// Send a log event to CloudWatch Logs using the log client.
        /// </summary>
        protected override async Task SendLogEventAsync(LogEvent logEvent)
        {
            await _logClient.SendLogEventAsync(logEvent);
        }

        /// <summary>
        /// Force flush any pending metrics.
        /// </summary>
        public override async Task ForceFlushAsync(CancellationToken cancellationToken)
        {
            await _logClient.FlushPendingEventsAsync();
        }

        /// <summary>
        /// Shutdown the exporter.
        /// </summary>
        public override async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            await ForceFlushAsync(cancellationToken);
        }
    }
}
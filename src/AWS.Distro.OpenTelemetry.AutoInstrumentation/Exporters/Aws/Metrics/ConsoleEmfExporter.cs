// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics
{
    /// <summary>
    /// OpenTelemetry metrics exporter for CloudWatch EMF format to console output.
    /// 
    /// This exporter converts OTel metrics into CloudWatch EMF logs and writes them
    /// to standard output instead of sending to CloudWatch Logs. This is useful for
    /// debugging, testing, or when you want to process EMF logs with other tools.
    /// 
    /// https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch_Embedded_Metric_Format_Specification.html
    /// </summary>
    public class ConsoleEmfExporter : EmfExporterBase
    {
        /// <summary>
        /// Constructor for the Console EMF exporter.
        /// </summary>
        /// <param name="namespaceName">CloudWatch namespace for metrics (defaults to "default")</param>
        public ConsoleEmfExporter(string namespaceName = "default")
            : base(namespaceName)
        {
        }

        /// <summary>
        /// This method writes the EMF log message to stdout, making it easy to
        /// capture and redirect the output for processing or debugging purposes.
        /// </summary>
        protected override Task SendLogEventAsync(LogEvent logEvent)
        {
            Console.WriteLine($"[EMF EXPORT] {DateTime.Now}: {logEvent.Message}");
            File.AppendAllText("/app/logs/emf-debug.log", $"[{DateTime.Now}] EMF Export: {logEvent.Message}\n");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Force flush any pending metrics.
        /// For this exporter, there is nothing to forceFlush.
        /// </summary>
        public override Task ForceFlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Shutdown the exporter.
        /// For this exporter, there is nothing to clean-up in order to shutdown.
        /// </summary>
        public override Task ShutdownAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
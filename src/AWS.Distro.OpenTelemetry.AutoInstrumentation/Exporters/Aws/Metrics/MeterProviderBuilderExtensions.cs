// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using Amazon.CloudWatchLogs;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics
{
    /// <summary>
    /// Extension methods for MeterProviderBuilder to add AWS EMF exporters.
    /// </summary>
    public static class MeterProviderBuilderExtensions
    {
        /// <summary>
        /// Adds AWS CloudWatch EMF exporter to the MeterProvider.
        /// </summary>
        /// <param name="builder">The MeterProviderBuilder instance.</param>
        /// <param name="namespaceName">CloudWatch namespace for metrics.</param>
        /// <param name="logGroupName">CloudWatch log group name.</param>
        /// <param name="logStreamName">CloudWatch log stream name (optional).</param>
        /// <param name="configure">Optional configuration action for CloudWatch Logs client.</param>
        /// <returns>The MeterProviderBuilder instance for chaining.</returns>
        public static MeterProviderBuilder AddAwsCloudWatchEmfExporter(
            this MeterProviderBuilder builder,
            string namespaceName = "default",
            string logGroupName = "aws/otel/metrics",
            string? logStreamName = null,
            Action<AmazonCloudWatchLogsConfig>? configure = null)
        {
            var config = new AmazonCloudWatchLogsConfig();
            configure?.Invoke(config);

            return builder.AddReader(new PeriodicExportingMetricReader(
                new AwsCloudWatchEmfExporter(namespaceName, logGroupName, logStreamName, config),
                exportIntervalMilliseconds: 60000)); // Export every 60 seconds
        }

        /// <summary>
        /// Adds Console EMF exporter to the MeterProvider for debugging purposes.
        /// </summary>
        /// <param name="builder">The MeterProviderBuilder instance.</param>
        /// <param name="namespaceName">CloudWatch namespace for metrics.</param>
        /// <returns>The MeterProviderBuilder instance for chaining.</returns>
        public static MeterProviderBuilder AddConsoleEmfExporter(
            this MeterProviderBuilder builder,
            string namespaceName = "default")
        {
            return builder.AddReader(new PeriodicExportingMetricReader(
                new ConsoleEmfExporter(namespaceName),
                exportIntervalMilliseconds: 5000)); // Export every 5 seconds for debugging
        }
    }
}
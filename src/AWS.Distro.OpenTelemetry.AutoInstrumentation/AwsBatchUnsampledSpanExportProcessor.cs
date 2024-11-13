// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

/// <summary>
/// AwsBatchUnsampledSpanExportProcessor class that functions similar to BatchActivityExportProcessor
/// having the same input parameters and the same default values. However, the difference is that
/// BatchActivityExportProcessor only exports sampled (or recorded) activities while AwsBatchUnsampledSpanExportProcessor
/// sets the AttributeAWSTraceFlagSampled to false if activity is not sampled and only exports unsampled activities.
/// </summary>
internal class AwsBatchUnsampledSpanExportProcessor : BatchExportProcessor<Activity>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AwsBatchUnsampledSpanExportProcessor"/> class.
    /// </summary>
    /// <param name="exporter"><inheritdoc cref="BatchExportProcessor{T}.BatchExportProcessor" path="/param[@name='exporter']"/></param>
    /// <param name="maxQueueSize"><inheritdoc cref="BatchExportProcessor{T}.BatchExportProcessor" path="/param[@name='maxQueueSize']"/></param>
    /// <param name="scheduledDelayMilliseconds"><inheritdoc cref="BatchExportProcessor{T}.BatchExportProcessor" path="/param[@name='scheduledDelayMilliseconds']"/></param>
    /// <param name="exporterTimeoutMilliseconds"><inheritdoc cref="BatchExportProcessor{T}.BatchExportProcessor" path="/param[@name='exporterTimeoutMilliseconds']"/></param>
    /// <param name="maxExportBatchSize"><inheritdoc cref="BatchExportProcessor{T}.BatchExportProcessor" path="/param[@name='maxExportBatchSize']"/></param>
    public AwsBatchUnsampledSpanExportProcessor(BaseExporter<Activity> exporter, int maxQueueSize = 2048, int scheduledDelayMilliseconds = 5000, int exporterTimeoutMilliseconds = 30000, int maxExportBatchSize = 512)
        : base(exporter, maxQueueSize, scheduledDelayMilliseconds, exporterTimeoutMilliseconds, maxExportBatchSize)
    {
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // TODO: There is an OTEL discussion to add BeforeEnd to allow us to write to spans. Below is a hack and goes
        // against the otel specs (not to edit span in OnEnd) but is required for the time being.
        // Add BeforeEnd to have a callback where the span is still writeable open-telemetry/opentelemetry-specification#1089
        // https://github.com/open-telemetry/opentelemetry-specification/issues/1089
        // https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/sdk.md#onendspan
        if (!data.Recorded)
        {
            data.SetTag(AttributeAWSTraceFlagSampled, "false");

            // Only exporting unsampled traces as this is the purpose of this processor.
            this.OnExport(data);
        }
    }
}
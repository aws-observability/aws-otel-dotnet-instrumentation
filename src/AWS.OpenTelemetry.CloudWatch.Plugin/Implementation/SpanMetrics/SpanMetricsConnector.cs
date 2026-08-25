// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation;
using OpenTelemetry;
using static AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.SpanMetrics.SpanMetricsAttributeKeys;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.SpanMetrics;

/// <summary>
/// Derives call count and duration metrics from every recorded span.
/// </summary>
internal sealed class SpanMetricsConnector : BaseProcessor<Activity>
{
    /// <summary>
    /// The meter name that must be registered with the application's meter provider.
    /// </summary>
    internal const string ScopeName = SpanMetricsConstants.ScopeName;

    private const string MetricsExporterEnvironmentVariable = "OTEL_METRICS_EXPORTER";
    private const string RecordingStatePropertyName =
        "AWS.OpenTelemetry.CloudWatchPluginOtel.SpanMetrics.RecordingState";

    private static readonly Meter Meter = new(SpanMetricsConstants.ScopeName, SpanMetricsConstants.LibraryVersion);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        SpanMetricsConstants.CallsName,
        unit: SpanMetricsConstants.CallsUnit);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        SpanMetricsConstants.DurationName,
        unit: SpanMetricsConstants.DurationUnit,
        description: null,
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = SpanMetricsConstants.DurationBucketBoundaries,
        });

    private readonly bool otlpMetricsExporterConfigured = IsOtlpMetricsExporterConfigured();
    private volatile bool enabled = true;

    /// <summary>
    /// Gets or sets a value indicating whether this processor records span metrics.
    /// </summary>
    internal bool Enabled
    {
        get => this.enabled;
        set => this.enabled = value;
    }

    /// <inheritdoc/>
    public override void OnStart(Activity data)
    {
        if (!this.Enabled)
        {
            return;
        }

        try
        {
            var callsEnabled = Calls.Enabled;
            var durationEnabled = Duration.Enabled;

            // When OTLP is active, only claim local derivation if both metrics can be emitted.
            // Otherwise leave the span unstamped so the backend can derive the complete pair.
            if (this.otlpMetricsExporterConfigured && (!callsEnabled || !durationEnabled))
            {
                return;
            }

            if (!callsEnabled && !durationEnabled)
            {
                return;
            }

            var recordingState = new RecordingState(
                callsEnabled,
                durationEnabled,
                this.otlpMetricsExporterConfigured);

            // Preserve the start-time decision so OnEnd cannot emit metrics for a span
            // that was deliberately left unstamped.
            data.SetCustomProperty(RecordingStatePropertyName, recordingState);

            if (recordingState.Stamped)
            {
                data.SetTag(SpanMetricsConstants.Schema, SpanMetricsConstants.SchemaVersion);
                data.SetTag(SpanMetricsConstants.LibraryVersionKey, SpanMetricsConstants.LibraryVersion);
            }
        }
        catch (Exception exception)
        {
            CloudWatchPluginEventSource.Log.SpanProcessingException(nameof(this.OnStart), exception);
        }
    }

    /// <inheritdoc/>
    public override void OnEnd(Activity data)
    {
        try
        {
            var recordingState = data.GetCustomProperty(RecordingStatePropertyName) as RecordingState;
            if (recordingState is null)
            {
                return;
            }

            if (!this.Enabled ||
                (recordingState.Stamped && (!Calls.Enabled || !Duration.Enabled)))
            {
                RemoveDeduplicationTags(data, recordingState);
                return;
            }

            var tags = this.BuildMetricAttributes(data);
            if (recordingState.CallsEnabled)
            {
                Calls.Add(1, tags);
            }

            if (recordingState.DurationEnabled)
            {
                Duration.Record(data.Duration.TotalSeconds, tags);
            }
        }
        catch (Exception exception)
        {
            CloudWatchPluginEventSource.Log.SpanProcessingException(nameof(this.OnEnd), exception);
        }
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        return true;
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = this.OnForceFlush(timeoutMilliseconds);
        this.Enabled = false;
        return result;
    }

    private static bool IsOtlpMetricsExporterConfigured()
    {
        var configuredExporters = Environment.GetEnvironmentVariable(MetricsExporterEnvironmentVariable);
        if (configuredExporters is null)
        {
            return true;
        }

        foreach (var exporter in configuredExporters.Split(','))
        {
            if (string.Equals(exporter.Trim(), "otlp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveDeduplicationTags(Activity data, RecordingState recordingState)
    {
        if (!recordingState.Stamped)
        {
            return;
        }

        data.SetTag(SpanMetricsConstants.Schema, null);
        data.SetTag(SpanMetricsConstants.LibraryVersionKey, null);
    }

    private static void Copy(ref TagList tags, Activity activity, string key)
    {
        var value = activity.GetTagItem(key);
        if (value is not null)
        {
            tags.Add(key, value);
        }
    }

    private static void Copy(ref TagList tags, Activity activity, string primaryKey, string fallbackKey)
    {
        var value = activity.GetTagItem(primaryKey);
        if (value is not null)
        {
            tags.Add(primaryKey, value);
            return;
        }

        Copy(ref tags, activity, fallbackKey);
    }

    private static void Copy(
        ref TagList tags,
        Activity activity,
        string primaryKey,
        string firstFallbackKey,
        string secondFallbackKey,
        string thirdFallbackKey,
        string fourthFallbackKey)
    {
        var value = activity.GetTagItem(primaryKey);
        if (value is not null)
        {
            tags.Add(primaryKey, value);
            return;
        }

        value = activity.GetTagItem(firstFallbackKey);
        if (value is not null)
        {
            tags.Add(firstFallbackKey, value);
            return;
        }

        value = activity.GetTagItem(secondFallbackKey);
        if (value is not null)
        {
            tags.Add(secondFallbackKey, value);
            return;
        }

        value = activity.GetTagItem(thirdFallbackKey);
        if (value is not null)
        {
            tags.Add(thirdFallbackKey, value);
            return;
        }

        Copy(ref tags, activity, fourthFallbackKey);
    }

    private static string GetSpanKind(ActivityKind kind)
    {
        return kind switch
        {
            ActivityKind.Internal => "INTERNAL",
            ActivityKind.Server => "SERVER",
            ActivityKind.Client => "CLIENT",
            ActivityKind.Producer => "PRODUCER",
            ActivityKind.Consumer => "CONSUMER",
            _ => kind.ToString().ToUpperInvariant(),
        };
    }

    private static string GetStatusCode(ActivityStatusCode status)
    {
        return status switch
        {
            ActivityStatusCode.Unset => "UNSET",
            ActivityStatusCode.Ok => "OK",
            ActivityStatusCode.Error => "ERROR",
            _ => status.ToString().ToUpperInvariant(),
        };
    }

    private TagList BuildMetricAttributes(Activity activity)
    {
        var tags = new TagList
        {
            { SpanMetricsConstants.SpanName, activity.DisplayName },
            { SpanMetricsConstants.SpanKind, GetSpanKind(activity.Kind) },
            { SpanMetricsConstants.StatusCode, GetStatusCode(activity.Status) },
            { SpanMetricsConstants.Schema, SpanMetricsConstants.SchemaVersion },
            { SpanMetricsConstants.LibraryVersionKey, SpanMetricsConstants.LibraryVersion },
        };

        Copy(ref tags, activity, AttributeHttpRequestMethod, AttributeHttpMethod);
        Copy(ref tags, activity, AttributeHttpResponseStatusCode, AttributeHttpStatusCode);
        Copy(ref tags, activity, AttributeHttpRoute);
        Copy(ref tags, activity, AttributeErrorType);
        Copy(ref tags, activity, AttributeRpcSystemName, AttributeRpcSystem);
        Copy(ref tags, activity, AttributeRpcService);
        Copy(ref tags, activity, AttributeRpcMethod);
        Copy(ref tags, activity, AttributeDbSystemName, AttributeDbSystem);
        Copy(ref tags, activity, AttributeDbOperationName, AttributeDbOperation);
        Copy(
            ref tags,
            activity,
            AttributeDbCollectionName,
            AttributeDbSqlTable,
            AttributeDbMongoDbCollection,
            AttributeDbCassandraTable,
            AttributeDbCosmosDbContainer);
        Copy(ref tags, activity, AttributeMessagingSystem);
        Copy(ref tags, activity, AttributeMessagingOperationName);

        if (activity.GetTagItem(AttributeMessagingDestinationTemporary) is not true &&
            activity.GetTagItem(AttributeMessagingDestinationAnonymous) is not true)
        {
            Copy(
                ref tags,
                activity,
                AttributeMessagingDestinationName,
                AttributeMessagingDestination);
        }

        return tags;
    }

    // Captures which span metrics were active when the span started.
    private sealed class RecordingState
    {
        public RecordingState(bool callsEnabled, bool durationEnabled, bool stamped)
        {
            this.CallsEnabled = callsEnabled;
            this.DurationEnabled = durationEnabled;
            this.Stamped = stamped;
        }

        // Controls whether OnEnd increments the calls counter.
        public bool CallsEnabled { get; }

        // Controls whether OnEnd records the duration histogram.
        public bool DurationEnabled { get; }

        // Indicates that deduplication tags were added to the exported span.
        public bool Stamped { get; }
    }
}

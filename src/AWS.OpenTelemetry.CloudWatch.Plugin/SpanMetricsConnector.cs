// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using static AWS.OpenTelemetry.CloudWatch.Plugin.SpanMetricsAttributeKeys;

namespace AWS.OpenTelemetry.CloudWatch.Plugin;

/// <summary>
/// Derives call count and duration metrics from every recorded span.
/// </summary>
public sealed class SpanMetricsConnector : BaseProcessor<Activity>
{
    /// <summary>
    /// The meter name that must be registered with the application's meter provider.
    /// </summary>
    public const string ScopeName = SpanMetricsConstants.ScopeName;

    private static readonly Meter Meter = new(SpanMetricsConstants.ScopeName, SpanMetricsConstants.LibraryVersion);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>(SpanMetricsConstants.CallsName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        SpanMetricsConstants.DurationName,
        unit: SpanMetricsConstants.DurationUnit,
        description: null,
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = SpanMetricsConstants.DurationBucketBoundaries,
        });

    private volatile bool enabled = true;

    /// <summary>
    /// Gets or sets a value indicating whether this processor records span metrics.
    /// </summary>
    public bool Enabled
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
            data.SetTag(SpanMetricsConstants.Schema, SpanMetricsConstants.SchemaVersion);
            data.SetTag(SpanMetricsConstants.LibraryVersionKey, SpanMetricsConstants.LibraryVersion);
        }
        catch (Exception)
        {
            // Telemetry processing must not affect the instrumented application.
        }
    }

    /// <inheritdoc/>
    public override void OnEnd(Activity data)
    {
        if (!this.Enabled)
        {
            return;
        }

        try
        {
            var tags = this.BuildMetricAttributes(data);
            Calls.Add(1, tags);
            Duration.Record(data.Duration.TotalSeconds, tags);
        }
        catch (Exception)
        {
            // Telemetry processing must not affect the instrumented application.
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

    private static void Copy(ref TagList tags, Activity activity, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = activity.GetTagItem(key);
            if (value is not null)
            {
                tags.Add(key, value);
                return;
            }
        }
    }

    private TagList BuildMetricAttributes(Activity activity)
    {
        var tags = new TagList
        {
            { SpanMetricsConstants.SpanName, activity.DisplayName },
            { SpanMetricsConstants.SpanKind, activity.Kind.ToString().ToUpperInvariant() },
            { SpanMetricsConstants.StatusCode, activity.Status.ToString().ToUpperInvariant() },
            { SpanMetricsConstants.Schema, SpanMetricsConstants.SchemaVersion },
            { SpanMetricsConstants.LibraryVersionKey, SpanMetricsConstants.LibraryVersion },
        };

        this.CopyServiceName(ref tags);
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

    private void CopyServiceName(ref TagList tags)
    {
        foreach (var attribute in this.ParentProvider.GetResource().Attributes)
        {
            if (attribute.Key == AttributeServiceName && attribute.Value is not null)
            {
                tags.Add(attribute.Key, attribute.Value);
                return;
            }
        }
    }
}

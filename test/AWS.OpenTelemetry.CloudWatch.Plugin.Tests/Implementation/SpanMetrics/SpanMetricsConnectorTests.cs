// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests.Implementation.SpanMetrics;

[Collection(SpanMetricsTestsCollection.Name)]
public class SpanMetricsConnectorTests
{
    private static readonly double[] ExpectedBoundaries =
    [
        0.002,
        0.004,
        0.006,
        0.008,
        0.01,
        0.05,
        0.1,
        0.2,
        0.4,
        0.8,
        1.0,
        1.4,
        2.0,
        5.0,
        10.0,
        15.0,
        double.PositiveInfinity,
    ];

    [Fact]
    public void SpanMetricsConnectorDerivesBothMetricsAndStampsExportedSpan()
    {
        using var pipeline = new TestPipeline(
            new AlwaysOnSampler(),
            ResourceBuilder.CreateEmpty().AddService("orders-service"));

        var activity = pipeline.Record(
            "GET /orders/{id}",
            ActivityKind.Server,
            span =>
            {
                span.SetStatus(ActivityStatusCode.Error);
                span.SetTag("http.request.method", "GET");
                span.SetTag("http.response.status_code", 500);
                span.SetTag("http.route", "/orders/{id}");
            });
        pipeline.Flush();

        var exported = Assert.Single(pipeline.ExportedActivities);
        Assert.Same(activity, exported);
        Assert.Equal("v1", exported.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Equal(
            SpanMetricsConstants.LibraryVersion,
            exported.GetTagItem("aws.otel.extension.lib.version"));

        var callsMetric = GetMetric(pipeline.Metrics, "traces.span.metrics.calls");
        var durationMetric = GetMetric(pipeline.Metrics, "traces.span.metrics.duration");
        var calls = GetPoint(pipeline.Metrics, "traces.span.metrics.calls", activity.DisplayName);
        var duration = GetPoint(pipeline.Metrics, "traces.span.metrics.duration", activity.DisplayName);
        var callTags = GetTags(calls);
        var durationTags = GetTags(duration);

        Assert.Equal(SpanMetricsConnector.ScopeName, callsMetric.MeterName);
        Assert.Equal(SpanMetricsConstants.LibraryVersion, callsMetric.MeterVersion);
        Assert.Equal(SpanMetricsConnector.ScopeName, durationMetric.MeterName);
        Assert.Equal(SpanMetricsConstants.LibraryVersion, durationMetric.MeterVersion);
        Assert.Equal(SpanMetricsConstants.DurationUnit, durationMetric.Unit);
        Assert.Equal(1, calls.GetSumLong());
        Assert.Equal(1, duration.GetHistogramCount());
        Assert.Equal(activity.Duration.TotalSeconds, duration.GetHistogramSum(), precision: 8);
        Assert.True(duration.TryGetHistogramMinMaxValues(out var minimum, out var maximum));
        Assert.Equal(activity.Duration.TotalSeconds, minimum, precision: 8);
        Assert.Equal(activity.Duration.TotalSeconds, maximum, precision: 8);
        Assert.Equal("orders-service", callTags["service.name"]);
        Assert.Equal("SERVER", callTags["span.kind"]);
        Assert.Equal("ERROR", callTags["status.code"]);
        Assert.Equal("GET", callTags["http.request.method"]);
        Assert.Equal(500, callTags["http.response.status_code"]);
        Assert.Equal("/orders/{id}", callTags["http.route"]);
        Assert.Equal("v1", callTags["aws.otel.span.metrics.schema"]);
        Assert.False(string.IsNullOrEmpty(callTags["aws.otel.extension.lib.version"] as string));
        AssertTagSetsEqual(callTags, durationTags);
        Assert.Equal(ExpectedBoundaries, GetHistogramBoundaries(duration));
    }

    [Fact]
    public void SpanMetricsConnectorIsOrderSafeAfterExportProcessor()
    {
        var exportedActivities = new List<Activity>();
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(exportedActivities)
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("ordered"))
        {
            Assert.NotNull(activity);
        }

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        var exported = Assert.Single(exportedActivities);
        Assert.Equal("v1", exported.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Equal(
            SpanMetricsConstants.LibraryVersion,
            exported.GetTagItem("aws.otel.extension.lib.version"));
        Assert.Equal(1, GetPoint(metrics, "traces.span.metrics.calls", "ordered").GetSumLong());
    }

    [Fact]
    public void SpanMetricsConnectorLeavesSpanStartedBeforeMeterProviderActivationUnstampedAndUncounted()
    {
        using var environment = new MetricsExporterEnvironment("otlp");
        var exportedActivities = new List<Activity>();
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(exportedActivities)
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);
        var activity = Assert.IsType<Activity>(source.StartActivity("provider-ordering"));

        Assert.Null(activity.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Null(activity.GetTagItem("aws.otel.extension.lib.version"));

        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        activity.Dispose();
        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        var exported = Assert.Single(exportedActivities);
        Assert.Null(exported.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Null(exported.GetTagItem("aws.otel.extension.lib.version"));
        Assert.False(HasPoint(metrics, "traces.span.metrics.calls", "provider-ordering"));
        Assert.False(HasPoint(metrics, "traces.span.metrics.duration", "provider-ordering"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("otlp", true)]
    [InlineData("OTLP", true)]
    [InlineData("console, otlp", true)]
    [InlineData("none", false)]
    [InlineData("console", false)]
    public void SpanMetricsConnectorStampsSpansOnlyForOtlpMetrics(
        string? configuredExporters,
        bool shouldStampSpan)
    {
        using var environment = new MetricsExporterEnvironment(configuredExporters);
        using var pipeline = new TestPipeline(new AlwaysOnSampler());

        var activity = pipeline.Record("activation", ActivityKind.Internal);
        pipeline.Flush();

        Assert.Equal(
            shouldStampSpan,
            activity.GetTagItem("aws.otel.span.metrics.schema") is not null);
        Assert.Equal(
            shouldStampSpan,
            activity.GetTagItem("aws.otel.extension.lib.version") is not null);
        Assert.Equal(
            1,
            GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "activation").GetSumLong());
    }

    [Fact]
    public void SpanMetricsConnectorLeavesSpanUnstampedWhenMetricsAreInactive()
    {
        using var environment = new MetricsExporterEnvironment("otlp");
        var exportedActivities = new List<Activity>();
        var sourceName = UniqueName();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(exportedActivities)
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("inactive"))
        {
            Assert.NotNull(activity);
        }

        tracerProvider.ForceFlush();

        var exported = Assert.Single(exportedActivities);
        Assert.Null(exported.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Null(exported.GetTagItem("aws.otel.extension.lib.version"));
    }

    [Fact]
    public void SpanMetricsConnectorRemovesStampsWhenMetricsDeactivateBeforeSpanEnds()
    {
        using var environment = new MetricsExporterEnvironment("otlp");
        var exportedActivities = new List<Activity>();
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(exportedActivities)
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);
        var activity = Assert.IsType<Activity>(source.StartActivity("deactivated"));

        Assert.Equal("v1", activity.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Equal(
            SpanMetricsConstants.LibraryVersion,
            activity.GetTagItem("aws.otel.extension.lib.version"));

        meterProvider.Dispose();
        activity.Dispose();
        tracerProvider.ForceFlush();

        var exported = Assert.Single(exportedActivities);
        Assert.Null(exported.GetTagItem("aws.otel.span.metrics.schema"));
        Assert.Null(exported.GetTagItem("aws.otel.extension.lib.version"));
        Assert.False(HasPoint(metrics, "traces.span.metrics.calls", "deactivated"));
        Assert.False(HasPoint(metrics, "traces.span.metrics.duration", "deactivated"));
    }

    [Fact]
    public void SpanMetricsConnectorAlwaysOffRecordsAllMetricsAndExportsNoSpans()
    {
        using var pipeline = new TestPipeline(new AlwaysOffSampler());
        var activities = new List<Activity>();

        for (var i = 0; i < 5; i++)
        {
            activities.Add(pipeline.Record("dropped", ActivityKind.Client));
        }

        pipeline.Flush();

        Assert.Empty(pipeline.ExportedActivities);
        Assert.All(
            activities,
            activity =>
            {
                Assert.Equal("v1", activity.GetTagItem("aws.otel.span.metrics.schema"));
                Assert.Equal(
                    SpanMetricsConstants.LibraryVersion,
                    activity.GetTagItem("aws.otel.extension.lib.version"));
            });
        Assert.Equal(5, GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "dropped").GetSumLong());
        var duration = GetPoint(pipeline.Metrics, "traces.span.metrics.duration", "dropped");
        Assert.Equal(5, duration.GetHistogramCount());
        Assert.True(duration.GetHistogramSum() > 0);
        Assert.True(duration.TryGetHistogramMinMaxValues(out var minimum, out var maximum));
        Assert.True(minimum > 0);
        Assert.True(maximum > 0);
    }

    [Fact]
    public void SpanMetricsConnectorUsesDefaultKindAndStatusAndOmitsMissingServiceName()
    {
        using var pipeline = new TestPipeline(
            new AlwaysOnSampler(),
            ResourceBuilder.CreateEmpty());
        pipeline.Record("defaults", ActivityKind.Internal);
        pipeline.Flush();

        var tags = GetTags(GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "defaults"));

        Assert.Equal(5, tags.Count);
        Assert.DoesNotContain("service.name", tags.Keys);
        Assert.Equal("defaults", tags["span.name"]);
        Assert.Equal("INTERNAL", tags["span.kind"]);
        Assert.Equal("UNSET", tags["status.code"]);
        Assert.Equal("v1", tags["aws.otel.span.metrics.schema"]);
        Assert.Equal(SpanMetricsConstants.LibraryVersion, tags["aws.otel.extension.lib.version"]);
    }

    [Theory]
    [InlineData(ActivityKind.Internal, "INTERNAL")]
    [InlineData(ActivityKind.Server, "SERVER")]
    [InlineData(ActivityKind.Client, "CLIENT")]
    [InlineData(ActivityKind.Producer, "PRODUCER")]
    [InlineData(ActivityKind.Consumer, "CONSUMER")]
    public void SpanMetricsConnectorMapsEverySpanKind(ActivityKind kind, string expected)
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record("kind-" + expected, kind);
        pipeline.Flush();

        var tags = GetTags(GetPoint(
            pipeline.Metrics,
            "traces.span.metrics.calls",
            "kind-" + expected));

        Assert.Equal(expected, tags["span.kind"]);
    }

    [Theory]
    [InlineData(ActivityStatusCode.Unset, "UNSET")]
    [InlineData(ActivityStatusCode.Ok, "OK")]
    [InlineData(ActivityStatusCode.Error, "ERROR")]
    public void SpanMetricsConnectorMapsEveryStatus(ActivityStatusCode status, string expected)
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "status-" + expected,
            ActivityKind.Internal,
            activity => activity.SetStatus(status));
        pipeline.Flush();

        var tags = GetTags(GetPoint(
            pipeline.Metrics,
            "traces.span.metrics.calls",
            "status-" + expected));

        Assert.Equal(expected, tags["status.code"]);
    }

    [Fact]
    public void SpanMetricsConnectorCanonicalAttributesTakePrecedence()
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "precedence",
            ActivityKind.Client,
            activity =>
            {
                activity.SetTag("http.request.method", "GET");
                activity.SetTag("http.method", "POST");
                activity.SetTag("http.response.status_code", 200);
                activity.SetTag("http.status_code", 500);
                activity.SetTag("rpc.system.name", "grpc");
                activity.SetTag("rpc.system", "apache_dubbo");
                activity.SetTag("db.system.name", "postgresql");
                activity.SetTag("db.system", "mysql");
                activity.SetTag("db.operation.name", "SELECT");
                activity.SetTag("db.operation", "INSERT");
                activity.SetTag("db.collection.name", "users");
                activity.SetTag("db.sql.table", "accounts");
                activity.SetTag("messaging.destination.name", "orders");
                activity.SetTag("messaging.destination", "legacy-orders");
            });
        pipeline.Flush();

        var tags = GetTags(GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "precedence"));

        Assert.Equal("GET", tags["http.request.method"]);
        Assert.Equal(200, tags["http.response.status_code"]);
        Assert.Equal("grpc", tags["rpc.system.name"]);
        Assert.Equal("postgresql", tags["db.system.name"]);
        Assert.Equal("SELECT", tags["db.operation.name"]);
        Assert.Equal("users", tags["db.collection.name"]);
        Assert.Equal("orders", tags["messaging.destination.name"]);
        Assert.DoesNotContain("http.method", tags.Keys);
        Assert.DoesNotContain("http.status_code", tags.Keys);
        Assert.DoesNotContain("rpc.system", tags.Keys);
        Assert.DoesNotContain("db.system", tags.Keys);
        Assert.DoesNotContain("db.operation", tags.Keys);
        Assert.DoesNotContain("db.sql.table", tags.Keys);
        Assert.DoesNotContain("messaging.destination", tags.Keys);
    }

    [Fact]
    public void SpanMetricsConnectorCopiesRemainingCanonicalAttributes()
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "canonical",
            ActivityKind.Client,
            activity =>
            {
                activity.SetTag("error.type", "timeout");
                activity.SetTag("rpc.service", "Greeter");
                activity.SetTag("rpc.method", "SayHello");
                activity.SetTag("messaging.system", "kafka");
                activity.SetTag("messaging.operation.name", "publish");
            });
        pipeline.Flush();

        var tags = GetTags(GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "canonical"));

        Assert.Equal("timeout", tags["error.type"]);
        Assert.Equal("Greeter", tags["rpc.service"]);
        Assert.Equal("SayHello", tags["rpc.method"]);
        Assert.Equal("kafka", tags["messaging.system"]);
        Assert.Equal("publish", tags["messaging.operation.name"]);
    }

    [Fact]
    public void SpanMetricsConnectorPreservesLegacyAttributeFamilies()
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "legacy",
            ActivityKind.Client,
            activity =>
            {
                activity.SetTag("http.method", "POST");
                activity.SetTag("http.status_code", 201);
                activity.SetTag("rpc.system", "grpc");
                activity.SetTag("rpc.service", "Greeter");
                activity.SetTag("rpc.method", "SayHello");
                activity.SetTag("db.system", "postgresql");
                activity.SetTag("db.operation", "SELECT");
                activity.SetTag("db.sql.table", "users");
                activity.SetTag("messaging.system", "kafka");
                activity.SetTag("messaging.operation", "publish");
                activity.SetTag("messaging.destination", "orders");
            });
        pipeline.Flush();

        var tags = GetTags(GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "legacy"));

        Assert.Equal("POST", tags["http.method"]);
        Assert.Equal(201, tags["http.status_code"]);
        Assert.Equal("grpc", tags["rpc.system"]);
        Assert.Equal("Greeter", tags["rpc.service"]);
        Assert.Equal("SayHello", tags["rpc.method"]);
        Assert.Equal("postgresql", tags["db.system"]);
        Assert.Equal("SELECT", tags["db.operation"]);
        Assert.Equal("users", tags["db.sql.table"]);
        Assert.Equal("kafka", tags["messaging.system"]);
        Assert.Equal("orders", tags["messaging.destination"]);
        Assert.DoesNotContain("http.request.method", tags.Keys);
        Assert.DoesNotContain("http.response.status_code", tags.Keys);
        Assert.DoesNotContain("rpc.system.name", tags.Keys);
        Assert.DoesNotContain("db.system.name", tags.Keys);
        Assert.DoesNotContain("db.operation.name", tags.Keys);
        Assert.DoesNotContain("db.collection.name", tags.Keys);
        Assert.DoesNotContain("messaging.operation", tags.Keys);
        Assert.DoesNotContain("messaging.destination.name", tags.Keys);
    }

    [Theory]
    [InlineData("messaging.destination.temporary", "messaging.destination.name")]
    [InlineData("messaging.destination.anonymous", "messaging.destination.name")]
    [InlineData("messaging.destination.temporary", "messaging.destination")]
    [InlineData("messaging.destination.anonymous", "messaging.destination")]
    public void SpanMetricsConnectorSuppressesEphemeralMessagingDestinations(
        string suppressionKey,
        string destinationKey)
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "ephemeral-" + suppressionKey + "-" + destinationKey,
            ActivityKind.Producer,
            activity =>
            {
                activity.SetTag("messaging.system", "kafka");
                activity.SetTag(destinationKey, "orders");
                activity.SetTag(suppressionKey, true);
            });
        pipeline.Flush();

        var tags = GetTags(GetPoint(
            pipeline.Metrics,
            "traces.span.metrics.calls",
            "ephemeral-" + suppressionKey + "-" + destinationKey));

        Assert.Equal("kafka", tags["messaging.system"]);
        Assert.DoesNotContain(destinationKey, tags.Keys);
    }

    [Fact]
    public void SpanMetricsConnectorUsesDbCollectionFallbackOrder()
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "collection",
            ActivityKind.Client,
            activity =>
            {
                activity.SetTag("db.sql.table", "sql-table");
                activity.SetTag("db.mongodb.collection", "mongo-collection");
                activity.SetTag("db.cassandra.table", "cassandra-table");
                activity.SetTag("db.cosmosdb.container", "cosmos-container");
            });
        pipeline.Flush();

        var tags = GetTags(GetPoint(pipeline.Metrics, "traces.span.metrics.calls", "collection"));

        Assert.Equal("sql-table", tags["db.sql.table"]);
    }

    [Theory]
    [InlineData("db.sql.table")]
    [InlineData("db.mongodb.collection")]
    [InlineData("db.cassandra.table")]
    [InlineData("db.cosmosdb.container")]
    public void SpanMetricsConnectorCopiesEachDbCollectionFallback(string collectionKey)
    {
        using var pipeline = new TestPipeline(new AlwaysOnSampler());
        pipeline.Record(
            "collection-" + collectionKey,
            ActivityKind.Client,
            activity => activity.SetTag(collectionKey, "users"));
        pipeline.Flush();

        var tags = GetTags(GetPoint(
            pipeline.Metrics,
            "traces.span.metrics.calls",
            "collection-" + collectionKey));

        Assert.Equal("users", tags[collectionKey]);
        Assert.DoesNotContain("db.collection.name", tags.Keys);
    }

    [Fact]
    public void SpanMetricsConnectorIgnoresMalformedCallbacks()
    {
        var processor = new SpanMetricsConnector();

        var exception = Record.Exception(() =>
        {
            processor.OnStart(null!);
            processor.OnEnd(null!);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void SpanMetricsConnectorRecordsWhenOnlyCallsIsEnabled()
    {
        using var environment = new MetricsExporterEnvironment("none");
        var measurements = new List<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SpanMetricsConnector.ScopeName &&
                instrument.Name == "traces.span.metrics.calls")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, state) => measurements.Add(measurement));
        meterListener.Start();

        var sourceName = UniqueName();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("calls-only"))
        {
            Assert.NotNull(activity);
            Assert.Null(activity.GetTagItem("aws.otel.span.metrics.schema"));
            Assert.Null(activity.GetTagItem("aws.otel.extension.lib.version"));
        }

        Assert.Single(measurements);
        Assert.Equal(1, measurements[0]);
    }

    [Fact]
    public void SpanMetricsConnectorRecordsWhenOnlyDurationIsEnabled()
    {
        using var environment = new MetricsExporterEnvironment("none");
        var measurements = new List<double>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SpanMetricsConnector.ScopeName &&
                instrument.Name == "traces.span.metrics.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, state) => measurements.Add(measurement));
        meterListener.Start();

        var sourceName = UniqueName();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SpanMetricsConnector())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("duration-only"))
        {
            Assert.NotNull(activity);
            Assert.Null(activity.GetTagItem("aws.otel.span.metrics.schema"));
            Assert.Null(activity.GetTagItem("aws.otel.extension.lib.version"));
        }

        Assert.Single(measurements);
    }

    [Fact]
    public void SpanMetricsConnectorShutdownDisablesFurtherRecording()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        var processor = new SpanMetricsConnector();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(processor)
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var before = source.StartActivity("before-shutdown"))
        {
            Assert.NotNull(before);
            Assert.Equal("v1", before.GetTagItem("aws.otel.span.metrics.schema"));
            Assert.Equal(
                SpanMetricsConstants.LibraryVersion,
                before.GetTagItem("aws.otel.extension.lib.version"));
        }

        Assert.True(processor.ForceFlush());
        Assert.True(processor.Shutdown());

        using (var after = source.StartActivity("after-shutdown"))
        {
            Assert.NotNull(after);
            Assert.Null(after.GetTagItem("aws.otel.span.metrics.schema"));
            Assert.Null(after.GetTagItem("aws.otel.extension.lib.version"));
        }

        meterProvider.ForceFlush();

        Assert.Equal(1, GetPoint(metrics, "traces.span.metrics.calls", "before-shutdown").GetSumLong());
        Assert.False(HasPoint(metrics, "traces.span.metrics.calls", "after-shutdown"));
    }

    internal static MetricPoint GetPoint(IEnumerable<Metric> metrics, string metricName, string spanName)
    {
        MetricPoint? result = null;
        foreach (var metric in metrics.Where(metric => metric.Name == metricName))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = GetTags(point);
                if (tags.TryGetValue("span.name", out var candidate) &&
                    string.Equals(candidate as string, spanName, StringComparison.Ordinal))
                {
                    Assert.Null(result);
                    result = point;
                }
            }
        }

        return result ?? throw new Xunit.Sdk.XunitException(
            $"No {metricName} metric point was found for span '{spanName}'.");
    }

    internal static Dictionary<string, object?> GetTags(MetricPoint point)
    {
        var tags = new Dictionary<string, object?>();
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value;
        }

        return tags;
    }

    private static Metric GetMetric(IEnumerable<Metric> metrics, string metricName)
    {
        return Assert.Single(metrics, metric => metric.Name == metricName);
    }

    private static bool HasPoint(IEnumerable<Metric> metrics, string metricName, string spanName)
    {
        foreach (var metric in metrics.Where(metric => metric.Name == metricName))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = GetTags(point);
                if (tags.TryGetValue("span.name", out var candidate) &&
                    string.Equals(candidate as string, spanName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string UniqueName()
    {
        return "span-metrics-tests-" + Guid.NewGuid().ToString("N");
    }

    private static double[] GetHistogramBoundaries(MetricPoint point)
    {
        var boundaries = new List<double>();
        foreach (var bucket in point.GetHistogramBuckets())
        {
            boundaries.Add(bucket.ExplicitBound);
        }

        return boundaries.ToArray();
    }

    private static void AssertTagSetsEqual(
        IReadOnlyDictionary<string, object?> expected,
        IReadOnlyDictionary<string, object?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var tag in expected)
        {
            Assert.True(actual.TryGetValue(tag.Key, out var actualValue));
            Assert.Equal(tag.Value, actualValue);
        }
    }

    private sealed class TestPipeline : IDisposable
    {
        private readonly MeterProvider meterProvider;
        private readonly TracerProvider tracerProvider;
        private readonly ActivitySource source;

        public TestPipeline(Sampler rootSampler, ResourceBuilder? resourceBuilder = null)
        {
            var sourceName = UniqueName();
            this.Metrics = new List<Metric>();
            this.ExportedActivities = new List<Activity>();
            this.meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(SpanMetricsConnector.ScopeName)
                .AddInMemoryExporter(this.Metrics)
                .Build();

            var tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(sourceName)
                .SetSampler(AlwaysRecordSampler.Create(rootSampler))
                .AddInMemoryExporter(this.ExportedActivities)
                .AddProcessor(new SpanMetricsConnector());
            if (resourceBuilder is not null)
            {
                tracerBuilder.SetResourceBuilder(resourceBuilder);
            }

            this.tracerProvider = tracerBuilder.Build();
            this.source = new ActivitySource(sourceName);
        }

        public List<Activity> ExportedActivities { get; }

        public List<Metric> Metrics { get; }

        public Activity Record(string name, ActivityKind kind, Action<Activity>? configure = null)
        {
            Activity? completed;
            using (var activity = this.source.StartActivity(name, kind))
            {
                completed = Assert.IsType<Activity>(activity);
                configure?.Invoke(completed);
                Thread.Sleep(1);
            }

            return completed;
        }

        public void Flush()
        {
            Assert.True(this.tracerProvider.ForceFlush());
            Assert.True(this.meterProvider.ForceFlush());
        }

        public void Dispose()
        {
            this.source.Dispose();
            this.tracerProvider.Dispose();
            this.meterProvider.Dispose();
        }
    }
}

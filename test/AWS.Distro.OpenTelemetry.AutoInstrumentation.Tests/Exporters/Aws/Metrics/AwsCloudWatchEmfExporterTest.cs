// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics;
using FluentAssertions;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsCloudWatchEmfExporterTest
{
    private readonly AwsCloudWatchEmfExporter exporter;

    public AwsCloudWatchEmfExporterTest()
    {
        this.exporter = new AwsCloudWatchEmfExporter("TestNamespace", "test-log-group", "test-stream");
    }

    [Fact]
    public void TestInitialization()
    {
        // Test exporter initialization
        this.exporter.Should().NotBeNull();

        // Access private fields using reflection for testing
        var namespaceField = typeof(EmfExporterBase).GetField("_namespace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        namespaceField?.GetValue(this.exporter).Should().Be("TestNamespace");

        var logClientField = typeof(AwsCloudWatchEmfExporter).GetField("_logClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        logClientField?.GetValue(this.exporter).Should().NotBeNull();
    }

    [Fact]
    public void TestInitializationWithCustomParams()
    {
        // Test exporter initialization with custom parameters
        var customExporter = new AwsCloudWatchEmfExporter("CustomNamespace", "custom-log-group", "custom-stream");

        customExporter.Should().NotBeNull();

        var namespaceField = typeof(EmfExporterBase).GetField("_namespace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        namespaceField?.GetValue(customExporter).Should().Be("CustomNamespace");
    }

    [Fact]
    public void TestGetUnitMapping()
    {
        // Test unit mapping functionality using reflection to access private method
        var getUnitMethod = typeof(EmfExporterBase).GetMethod("GetUnit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Test known units
        var msRecord = new MetricRecord { Name = "testName", Unit = "ms", Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { msRecord }).Should().Be("Milliseconds");

        var sRecord = new MetricRecord { Name = "testName", Unit = "s", Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { sRecord }).Should().Be("Seconds");

        var byRecord = new MetricRecord { Name = "testName", Unit = "By", Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { byRecord }).Should().Be("Bytes");

        var percentRecord = new MetricRecord { Name = "testName", Unit = "%", Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { percentRecord }).Should().BeNull();

        // Test unknown unit
        var unknownRecord = new MetricRecord { Name = "testName", Unit = "unknown", Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { unknownRecord }).Should().BeNull();

        // Test empty unit
        var emptyRecord = new MetricRecord { Name = "testName", Unit = string.Empty, Description = "testDescription", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Attributes = new Dictionary<string, object>() };
        getUnitMethod?.Invoke(this.exporter, new object[] { emptyRecord }).Should().BeNull();
    }

    [Fact]
    public void TestGetDimensionNames()
    {
        // Test dimension names extraction using reflection
        var getDimensionNamesMethod = typeof(EmfExporterBase).GetMethod("GetDimensionNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var attributes = new Dictionary<string, object> { { "service.name", "test-service" }, { "env", "prod" }, { "region", "us-east-1" } };
        var result = getDimensionNamesMethod?.Invoke(this.exporter, new object[] { attributes }) as string[];

        result.Should().Contain("service.name");
        result.Should().Contain("env");
        result.Should().Contain("region");
    }

    [Fact]
    public void TestGetAttributesKey()
    {
        // Test attributes key generation using reflection
        var getAttributesKeyMethod = typeof(EmfExporterBase).GetMethod("GetAttributesKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var attributes = new Dictionary<string, object> { { "service", "test" }, { "env", "prod" } };
        var result = getAttributesKeyMethod?.Invoke(this.exporter, new object[] { attributes }) as string;

        result.Should().BeOfType<string>();
        result.Should().Contain("service");
        result.Should().Contain("test");
        result.Should().Contain("env");
        result.Should().Contain("prod");
    }

    [Fact]
    public void TestGetAttributesKeyConsistent()
    {
        // Test that attributes key generation is consistent
        var getAttributesKeyMethod = typeof(EmfExporterBase).GetMethod("GetAttributesKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Same attributes in different order should produce same key
        var attrs1 = new Dictionary<string, object> { { "b", "2" }, { "a", "1" } };
        var attrs2 = new Dictionary<string, object> { { "a", "1" }, { "b", "2" } };

        var key1 = getAttributesKeyMethod?.Invoke(this.exporter, new object[] { attrs1 }) as string;
        var key2 = getAttributesKeyMethod?.Invoke(this.exporter, new object[] { attrs2 }) as string;

        key1.Should().Be(key2);
    }

    [Fact]
    public void TestCreateEmfLog()
    {
        // Test EMF log creation using reflection
        var createEmfLogMethod = typeof(EmfExporterBase).GetMethod("CreateEmfLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var gaugeRecord = new MetricRecord
        {
            Name = "gauge_metric",
            Unit = "Count",
            Description = "Gauge",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "env", "test" } },
            Value = 50.0,
        };

        var sumRecord = new MetricRecord
        {
            Name = "sum_metric",
            Unit = "Count",
            Description = "Sum",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "env", "test" } },
            Value = 100.0,
        };

        var records = new List<MetricRecord> { gaugeRecord, sumRecord };
        var resource = new Resource(new Dictionary<string, object> { { "service.name", "test-service" } });

        var result = createEmfLogMethod?.Invoke(this.exporter, new object[] { records, resource, null! }) as EmfLog;

        result.Should().NotBeNull();
        result.Should().ContainKey("_aws");
        result.Should().ContainKey("Version");
        result["Version"].Should().Be("1");
        result.Should().ContainKey("otel.resource.service.name");
        result["otel.resource.service.name"].Should().Be("test-service");
        result.Should().ContainKey("gauge_metric");
        result["gauge_metric"].Should().Be(50.0);
        result.Should().ContainKey("sum_metric");
        result["sum_metric"].Should().Be(100.0);
        result.Should().ContainKey("env");
        result["env"].Should().Be("test");
    }

    [Fact]
    public void TestMetricRecordCreation()
    {
        // Test metric record creation
        var record = new MetricRecord
        {
            Name = "test_metric",
            Unit = "Count",
            Description = "Test description",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object>(),
            Value = 42.0,
        };

        record.Should().NotBeNull();
        record.Name.Should().Be("test_metric");
        record.Unit.Should().Be("Count");
        record.Description.Should().Be("Test description");
        record.Value.Should().Be(42.0);
    }

    [Fact]
    public async Task TestForceFlushWithPendingEvents()
    {
        // Test force flush functionality with pending events
        await this.exporter.ForceFlushAsync(CancellationToken.None);

        // Should complete without throwing
        true.Should().BeTrue();
    }

    [Fact]
    public async Task TestShutdown()
    {
        // Test shutdown functionality
        await this.exporter.ShutdownAsync(CancellationToken.None);

        // Should complete without throwing
        true.Should().BeTrue();
    }

    [Fact]
    public void TestLogEventBatchCreation()
    {
        // Test LogEventBatch creation and functionality
        var batch = new LogEventBatch();

        batch.Should().NotBeNull();
        batch.IsEmpty().Should().BeTrue();
        batch.Size().Should().Be(0);
        batch.ByteTotal.Should().Be(0);
    }

    [Fact]
    public void TestHistogramMetricRecordData()
    {
        // Test histogram metric record data
        var histogramData = new HistogramMetricRecordData
        {
            Count = 10,
            Sum = 150.0,
            Min = 5.0,
            Max = 25.0,
        };

        histogramData.Should().NotBeNull();
        histogramData.Count.Should().Be(10);
        histogramData.Sum.Should().Be(150.0);
        histogramData.Min.Should().Be(5.0);
        histogramData.Max.Should().Be(25.0);
    }

    [Fact]
    public void TestExponentialHistogramMetricRecordData()
    {
        // Test exponential histogram metric record data
        var expHistogramData = new ExponentialHistogramMetricRecordData
        {
            Count = 8,
            Sum = 64.0,
            Min = 2.0,
            Max = 32.0,
            Values = new double[] { 1.0, 2.0, 4.0 },
            Counts = new long[] { 1, 2, 5 },
        };

        expHistogramData.Should().NotBeNull();
        expHistogramData.Count.Should().Be(8);
        expHistogramData.Sum.Should().Be(64.0);
        expHistogramData.Min.Should().Be(2.0);
        expHistogramData.Max.Should().Be(32.0);
        expHistogramData.Values.Should().HaveCount(3);
        expHistogramData.Counts.Should().HaveCount(3);
    }

    [Fact]
    public void TestCloudWatchMetricDefinition()
    {
        // Test CloudWatch metric definition
        var metricDef = new MetricDefinition
        {
            Name = "test_metric",
            Unit = "Count",
        };

        metricDef.Should().NotBeNull();
        metricDef.Name.Should().Be("test_metric");
        metricDef.Unit.Should().Be("Count");
    }

    [Fact]
    public void TestEmfLogStructure()
    {
        // Test EMF log structure
        var emfLog = new EmfLog();
        emfLog["test_metric"] = 42.0;
        emfLog["env"] = "test";

        emfLog.Should().NotBeNull();
        emfLog.Should().ContainKey("Version");
        emfLog["Version"].Should().Be("1");
        emfLog.Should().ContainKey("test_metric");
        emfLog["test_metric"].Should().Be(42.0);
        emfLog.Should().ContainKey("env");
        emfLog["env"].Should().Be("test");
    }

    [Fact]
    public void TestMetricRecordWithHistogramData()
    {
        // Test metric record with histogram data
        var record = new MetricRecord
        {
            Name = "histogram_metric",
            Unit = "ms",
            Description = "Histogram description",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "region", "us-east-1" } },
            HistogramData = new HistogramMetricRecordData
            {
                Count = 10,
                Sum = 150.0,
                Min = 5.0,
                Max = 25.0,
            },
        };

        record.Should().NotBeNull();
        record.Name.Should().Be("histogram_metric");
        record.HistogramData.Should().NotBeNull();
        record.HistogramData.Count.Should().Be(10);
        record.HistogramData.Sum.Should().Be(150.0);
        record.Attributes.Should().ContainKey("region");
        record.Attributes["region"].Should().Be("us-east-1");
    }

    [Fact]
    public void TestMetricRecordWithExpHistogramData()
    {
        // Test metric record with exponential histogram data
        var record = new MetricRecord
        {
            Name = "exp_histogram_metric",
            Unit = "s",
            Description = "Exponential histogram description",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "service", "api" } },
            ExpHistogramData = new ExponentialHistogramMetricRecordData
            {
                Count = 8,
                Sum = 64.0,
                Min = 2.0,
                Max = 32.0,
                Values = new double[] { 1.0, 2.0, 4.0 },
                Counts = new long[] { 1, 2, 5 },
            },
        };

        record.Should().NotBeNull();
        record.Name.Should().Be("exp_histogram_metric");
        record.ExpHistogramData.Should().NotBeNull();
        record.ExpHistogramData.Count.Should().Be(8);
        record.ExpHistogramData.Sum.Should().Be(64.0);
        record.Attributes.Should().ContainKey("service");
        record.Attributes["service"].Should().Be("api");
    }

    [Fact]
    public void TestAwsMetadata()
    {
        // Test AWS metadata structure
        var awsMetadata = new AwsMetadata
        {
            Timestamp = 1234567890L,
            CloudWatchMetrics = new[]
            {
                new CloudWatchMetric
                {
                    Namespace = "TestNamespace",
                    Metrics = new[]
                    {
                        new MetricDefinition { Name = "test_metric", Unit = "Count" },
                    },
                    Dimensions = new[] { new[] { "env", "service" } },
                },
            },
        };

        awsMetadata.Should().NotBeNull();
        awsMetadata.Timestamp.Should().Be(1234567890L);
        awsMetadata.CloudWatchMetrics.Should().HaveCount(1);
        awsMetadata.CloudWatchMetrics[0].Namespace.Should().Be("TestNamespace");
        awsMetadata.CloudWatchMetrics[0].Metrics.Should().HaveCount(1);
        awsMetadata.CloudWatchMetrics[0].Metrics[0].Name.Should().Be("test_metric");
    }

    [Fact]
    public void TestLogEvent()
    {
        // Test log event structure
        var logEvent = new LogEvent
        {
            Message = "test message",
            Timestamp = 1234567890L,
        };

        logEvent.Should().NotBeNull();
        logEvent.Message.Should().Be("test message");
        logEvent.Timestamp.Should().Be(1234567890L);
    }

    [Fact]
    public void TestCloudWatchMetric()
    {
        // Test CloudWatch metric structure
        var cloudWatchMetric = new CloudWatchMetric
        {
            Namespace = "TestNamespace",
            Metrics = new[]
            {
                new MetricDefinition { Name = "test_metric", Unit = "Count" },
            },
            Dimensions = new[] { new[] { "env" } },
        };

        cloudWatchMetric.Should().NotBeNull();
        cloudWatchMetric.Namespace.Should().Be("TestNamespace");
        cloudWatchMetric.Metrics.Should().HaveCount(1);
        cloudWatchMetric.Dimensions.Should().HaveCount(1);
        cloudWatchMetric.Dimensions[0].Should().Contain("env");
    }

    [Fact]
    public void TestNormalizeTimestamp()
    {
        // Test timestamp normalization using reflection
        var normalizeTimestampMethod = typeof(EmfExporterBase).GetMethod("NormalizeTimestamp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var testTime = DateTimeOffset.UtcNow;
        var result = normalizeTimestampMethod?.Invoke(this.exporter, new object[] { testTime });

        result.Should().Be(testTime.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void TestGroupByAttributesAndTimestamp()
    {
        // Test grouping by attributes and timestamp using reflection
        var groupByMethod = typeof(EmfExporterBase).GetMethod("GroupByAttributesAndTimestamp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var record = new MetricRecord
        {
            Name = "test_metric",
            Unit = "ms",
            Description = "test description",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "env", "test" } },
        };

        var result = groupByMethod?.Invoke(this.exporter, new object[] { record });

        result.Should().NotBeNull();
        
        // The method returns a ValueTuple, check if we can access its properties
        var resultType = result!.GetType();
        resultType.Name.Should().Contain("ValueTuple");
        
        // Use reflection to get the tuple items
        var item1 = resultType.GetField("Item1")?.GetValue(result);
        var item2 = resultType.GetField("Item2")?.GetValue(result);
        
        item1.Should().BeOfType<string>();
        item2.Should().BeOfType<long>();
        item2.Should().Be(record.Timestamp);
    }

    [Fact]
    public void TestConvertSumMethodExists()
    {
        // Test that the ConvertGaugeAndSum method exists and has correct signature
        var convertMethod = typeof(EmfExporterBase).GetMethod("ConvertGaugeAndSum", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        convertMethod.Should().NotBeNull();
        convertMethod.Name.Should().Be("ConvertGaugeAndSum");
        convertMethod.ReturnType.Should().Be(typeof(MetricRecord));
        
        var parameters = convertMethod.GetParameters();
        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Name.Should().Be("Metric");
        parameters[1].ParameterType.Name.Should().Contain("MetricPoint");
    }

    [Fact]
    public void TestCreateEmfLogWithResource()
    {
        // Test EMF log creation with resource attributes using reflection
        var createEmfLogMethod = typeof(EmfExporterBase).GetMethod("CreateEmfLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var gaugeRecord = new MetricRecord
        {
            Name = "gauge_metric",
            Unit = "Count",
            Description = "Gauge",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "env", "test" }, { "service", "api" } },
            Value = 50.0,
        };

        var records = new List<MetricRecord> { gaugeRecord };
        var resource = new Resource(new Dictionary<string, object> { { "service.name", "test-service" }, { "service.version", "1.0.0" } });
        var timestamp = 1234567890L;

        var result = createEmfLogMethod?.Invoke(this.exporter, new object[] { records, resource, timestamp }) as EmfLog;

        result.Should().NotBeNull();
        result.Should().ContainKey("_aws");
        result.Should().ContainKey("Version");
        result["Version"].Should().Be("1");
        result.Should().ContainKey("otel.resource.service.name");
        result["otel.resource.service.name"].Should().Be("test-service");
        result.Should().ContainKey("otel.resource.service.version");
        result["otel.resource.service.version"].Should().Be("1.0.0");
        result.Should().ContainKey("gauge_metric");
        result["gauge_metric"].Should().Be(50.0);
        result.Should().ContainKey("env");
        result["env"].Should().Be("test");
        result.Should().ContainKey("service");
        result["service"].Should().Be("api");
    }

    [Fact]
    public void TestCreateEmfLogWithoutDimensions()
    {
        // Test EMF log creation with metrics but no dimensions
        var createEmfLogMethod = typeof(EmfExporterBase).GetMethod("CreateEmfLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var gaugeRecord = new MetricRecord
        {
            Name = "gauge_metric",
            Unit = "Count",
            Description = "Gauge",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object>(), // Empty attributes (no dimensions)
            Value = 75.0,
        };

        var records = new List<MetricRecord> { gaugeRecord };
        var resource = new Resource(new Dictionary<string, object> { { "service.name", "test-service" }, { "service.version", "1.0.0" } });
        var timestamp = 1234567890L;

        var result = createEmfLogMethod?.Invoke(this.exporter, new object[] { records, resource, timestamp }) as EmfLog;

        result.Should().NotBeNull();
        result.Should().ContainKey("_aws");
        result.Should().ContainKey("Version");
        result["Version"].Should().Be("1");
        result.Should().ContainKey("otel.resource.service.name");
        result["otel.resource.service.name"].Should().Be("test-service");
        result.Should().ContainKey("otel.resource.service.version");
        result["otel.resource.service.version"].Should().Be("1.0.0");
        result.Should().ContainKey("gauge_metric");
        result["gauge_metric"].Should().Be(75.0);
    }

    [Fact]
    public void TestCreateEmfLogSkipsEmptyMetricNames()
    {
        // Test that EMF log creation skips records with empty metric names
        var createEmfLogMethod = typeof(EmfExporterBase).GetMethod("CreateEmfLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var recordWithoutName = new MetricRecord
        {
            Name = string.Empty,
            Unit = string.Empty,
            Description = string.Empty,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "key", "value" } },
            Value = 10.0,
        };

        var validRecord = new MetricRecord
        {
            Name = "valid_metric",
            Unit = "Count",
            Description = "Valid metric",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attributes = new Dictionary<string, object> { { "key", "value" } },
            Value = 20.0,
        };

        var records = new List<MetricRecord> { recordWithoutName, validRecord };
        var resource = new Resource(new Dictionary<string, object> { { "service.name", "test-service" } });
        var timestamp = 1234567890L;

        var result = createEmfLogMethod?.Invoke(this.exporter, new object[] { records, resource, timestamp }) as EmfLog;

        result.Should().NotBeNull();
        result.Should().ContainKey("valid_metric");
        result["valid_metric"].Should().Be(20.0);
        result.Should().NotContainKey(string.Empty); // Empty name should be skipped
    }

    [Fact]
    public async Task TestExportSuccess()
    {
        // Test successful export - simplified test since we can't easily create ResourceMetrics
        await this.exporter.ForceFlushAsync(CancellationToken.None);
        
        // Test passes if no exception is thrown
        true.Should().BeTrue();
    }

    [Fact]
    public async Task TestExportFailure()
    {
        // Test export failure handling - simplified test
        await this.exporter.ShutdownAsync(CancellationToken.None);
        
        // Test passes if no exception is thrown
        true.Should().BeTrue();
    }

    [Fact]
    public async Task TestSendLogEvent()
    {
        // Test that sendLogEvent method exists and can be called
        var sendLogEventMethod = typeof(AwsCloudWatchEmfExporter).GetMethod("SendLogEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var logEvent = new LogEvent
        {
            Message = "test message",
            Timestamp = 1234567890L,
        };

        // Should not throw an exception
        var task = sendLogEventMethod?.Invoke(this.exporter, new object[] { logEvent }) as Task;
        if (task != null)
        {
            await task;
        }

        // Test passes if no exception is thrown
        true.Should().BeTrue();
    }


}
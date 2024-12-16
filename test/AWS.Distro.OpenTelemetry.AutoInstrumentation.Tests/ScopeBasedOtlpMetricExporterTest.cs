// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// ScopeBasedOtlpMetricExporter test class
/// </summary>
public class ScopeBasedOtlpMetricExporterTest
{
    private readonly string retainedScopeName = "test.retained";
    private readonly string droppedScopeName = "test.drop";

    [Fact]
    public void TestExport()
    {
        var opts = new ScopeBasedOtlpMetricExporter.ScopeBasedOtlpExporterOptions()
        {
            RegisteredScopeNames = new HashSet<string>() { this.retainedScopeName },
        };

        var exportAssert = new ExportAssert(6, this.retainedScopeName);
        var exporter = new ScopeBasedOtlpMetricExporter(opts, (batch) =>
        {
            exportAssert.Verify(batch);
            return ExportResult.Success;
        });

        var sdk = Sdk.CreateMeterProviderBuilder()
            .AddMeter(this.retainedScopeName)
            .AddMeter(this.droppedScopeName)
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();

        this.GenerateMetrics(6, 12);

        sdk.Dispose();
        Thread.Sleep(10);

        Assert.True(exportAssert.Success);
    }

    private void GenerateMetrics(int retained, int dropped)
    {
        var meter = new Meter(this.retainedScopeName);
        for (var i = 0; i < retained; i++)
        {
            var counter = meter.CreateCounter<int>("test.counter." + i);
            counter.Add(1);
        }

        meter = new Meter(this.droppedScopeName);
        for (var i = 0; i < dropped; i++)
        {
            var counter = meter.CreateCounter<int>("test.counter." + i);
            counter.Add(1);
        }
    }

    private class ExportAssert
    {
        private readonly int expectedCount;
        private readonly string registeredMeterName;

        internal ExportAssert(int expectedCount, string registeredMeterName)
        {
            this.expectedCount = expectedCount;
            this.registeredMeterName = registeredMeterName;
        }

        public bool Success { get; private set; }

        public void Verify(Batch<Metric> batch)
        {
            this.Success = true;
            if (batch.Count != this.expectedCount)
            {
                this.Success = false;
            }

            foreach (var m in batch)
            {
                if (m.MeterName != this.registeredMeterName)
                {
                    this.Success = false;
                }
            }
        }
    }
}

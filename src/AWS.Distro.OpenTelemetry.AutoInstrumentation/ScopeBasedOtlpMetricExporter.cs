// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A custom metric exporter that extends <see cref="OtlpMetricExporter"/>.
/// Exports metrics only for a specific instrumentation scope, filtering based on
/// the configured scope name. This allows selective metric exporting to the OTLP endpoint,
/// limiting exports to metrics that match the specified scope.
/// </summary>
public class ScopeBasedOtlpMetricExporter : OtlpMetricExporter
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<ScopeBasedOtlpMetricExporter>();

    private readonly HashSet<string> registeredScopedNames;
    private readonly Func<Batch<Metric>, ExportResult> exportHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeBasedOtlpMetricExporter"/> class.
    /// </summary>
    /// <param name="options">Configuration options for the exporter.</param>
    public ScopeBasedOtlpMetricExporter(ScopeBasedOtlpExporterOptions options)
        : base(options)
    {
        this.registeredScopedNames = options.RegisteredScopeNames ?? new HashSet<string>();
        this.exportHandler = batch => base.Export(batch);
    }

    internal ScopeBasedOtlpMetricExporter(
        ScopeBasedOtlpExporterOptions options,
        Func<Batch<Metric>, ExportResult> exportHandler)
        : base(options)
    {
        this.registeredScopedNames = options.RegisteredScopeNames ?? new HashSet<string>();
        this.exportHandler = exportHandler;
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Metric> metrics)
    {
        var exportingMetrics = new List<Metric>();
        foreach (var metric in metrics)
        {
            if (this.registeredScopedNames.Contains(metric.MeterName))
            {
                exportingMetrics.Add(metric);
            }
        }

        if (exportingMetrics.Count == 0)
        {
            Logger.Log(LogLevel.Debug, "No metrics to export.");
            return ExportResult.Success;
        }

        return this.exportHandler.Invoke(new Batch<Metric>(exportingMetrics.ToArray(), exportingMetrics.Count));
    }

    /// <summary>
    /// Scope based OTLP exporter options.
    /// </summary>
    public class ScopeBasedOtlpExporterOptions : OtlpExporterOptions
    {
        /// <summary>
        /// Gets or sets registered meter names whose metrics will be reserved.
        /// </summary>
        public HashSet<string>? RegisteredScopeNames { get; set; }
    }
}

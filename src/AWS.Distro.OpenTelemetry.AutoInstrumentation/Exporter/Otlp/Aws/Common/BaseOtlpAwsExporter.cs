// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

/// <summary>
/// Delegates OTLP exports and lifecycle operations to an upstream exporter.
/// </summary>
/// <typeparam name="T">The signal exported by the upstream exporter.</typeparam>
public abstract class BaseOtlpAwsExporter<T> : BaseExporter<T>
    where T : class
{
    // The SDK sets ParentProvider on the outer exporter only. Its internal setter is
    // needed on the delegate so upstream serialization can read the same resource.
    private static readonly PropertyInfo ParentProviderProperty = typeof(BaseExporter<T>).GetProperty(nameof(ParentProvider))!;
    private readonly BaseExporter<T> exporter;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseOtlpAwsExporter{T}"/> class.
    /// </summary>
    /// <param name="exporter">The upstream exporter to delegate to.</param>
    protected BaseOtlpAwsExporter(BaseExporter<T> exporter)
    {
        this.exporter = exporter;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<T> batch)
    {
        if (!ReferenceEquals(this.exporter.ParentProvider, this.ParentProvider))
        {
            ParentProviderProperty.SetValue(this.exporter, this.ParentProvider);
        }

        return this.exporter.Export(batch);
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds) => this.exporter.ForceFlush(timeoutMilliseconds);

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds) => this.exporter.Shutdown(timeoutMilliseconds);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.exporter.Dispose();
            this.disposed = true;
        }

        base.Dispose(disposing);
    }
}

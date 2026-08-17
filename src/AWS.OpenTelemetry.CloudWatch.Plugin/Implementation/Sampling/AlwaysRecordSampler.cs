// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;

/// <summary>
/// Converts drop decisions from another sampler to record-only decisions.
/// </summary>
/// <remarks>
/// This allows processors to observe every span without increasing the number of exported spans.
/// </remarks>
internal sealed class AlwaysRecordSampler : Sampler
{
    private readonly Sampler rootSampler;
    private volatile bool enabled = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlwaysRecordSampler"/> class
    /// using the OpenTelemetry SDK default sampling policy.
    /// </summary>
    internal AlwaysRecordSampler()
        : this(new ParentBasedSampler(new AlwaysOnSampler()))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AlwaysRecordSampler"/> class.
    /// </summary>
    /// <param name="rootSampler">The application sampler whose export decisions are preserved.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rootSampler"/> is null.</exception>
    internal AlwaysRecordSampler(Sampler rootSampler)
    {
        this.rootSampler = rootSampler ?? throw new ArgumentNullException(nameof(rootSampler));
        this.Description = "AlwaysRecordSampler{" + rootSampler.Description + "}";
    }

    /// <summary>
    /// Gets or sets a value indicating whether drop decisions are converted to record-only decisions.
    /// </summary>
    internal bool Enabled
    {
        get => this.enabled;
        set => this.enabled = value;
    }

    /// <inheritdoc/>
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var result = this.rootSampler.ShouldSample(samplingParameters);
        if (!this.Enabled || result.Decision != SamplingDecision.Drop)
        {
            return result;
        }

        return new SamplingResult(
            SamplingDecision.RecordOnly,
            result.Attributes,
            result.TraceStateString);
    }
}

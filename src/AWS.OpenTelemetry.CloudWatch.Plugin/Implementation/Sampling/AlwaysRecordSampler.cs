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

    private AlwaysRecordSampler(Sampler rootSampler)
    {
        this.rootSampler = rootSampler ?? throw new ArgumentNullException(nameof(rootSampler));
        this.Description = "AlwaysRecordSampler{" + rootSampler.Description + "}";
    }

    /// <inheritdoc/>
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var result = this.rootSampler.ShouldSample(samplingParameters);
        if (result.Decision == SamplingDecision.Drop)
        {
            result = WrapResultWithRecordOnlyResult(result);
        }

        return result;
    }

    /// <summary>
    /// Creates an <see cref="AlwaysRecordSampler"/> that preserves the supplied sampler's export decisions.
    /// </summary>
    /// <param name="rootSampler">The application sampler whose export decisions are preserved.</param>
    /// <returns>A sampler that converts drop decisions to record-only decisions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rootSampler"/> is null.</exception>
    internal static AlwaysRecordSampler Create(Sampler rootSampler)
    {
        return new AlwaysRecordSampler(rootSampler);
    }

    private static SamplingResult WrapResultWithRecordOnlyResult(SamplingResult result)
    {
        return new SamplingResult(SamplingDecision.RecordOnly, result.Attributes, result.TraceStateString);
    }
}

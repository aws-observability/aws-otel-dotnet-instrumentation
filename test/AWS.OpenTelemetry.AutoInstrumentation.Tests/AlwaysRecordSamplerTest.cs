// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Moq;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// AlwaysRecordSamplerTest test class
/// </summary>
public class AlwaysRecordSamplerTest
{
    /// <summary>
    /// Tests Description is set properly with AlwaysRecordSampler keyword
    /// </summary>
    [Fact]
    public void TestGetDescription()
    {
        var mockSampler = new Mock<Sampler>();
        var sampler = AlwaysRecordSampler.Create(mockSampler.Object);
        Assert.Equal("AlwaysRecordSampler{SamplerProxy}", sampler.Description);
    }

    /// <summary>
    /// TestRecordAndSampleSamplingDecision
    /// </summary>
    [Fact]
    public void TestRecordAndSampleSamplingDecision()
    {
        this.ValidateShouldSample(SamplingDecision.RecordAndSample, SamplingDecision.RecordAndSample);
    }

    /// <summary>
    /// TestRecordOnlySamplingDecision
    /// </summary>
    [Fact]
    public void TestRecordOnlySamplingDecision()
    {
        this.ValidateShouldSample(SamplingDecision.RecordOnly, SamplingDecision.RecordOnly);
    }

    /// <summary>
    /// TestDropSamplingDecision
    /// </summary>
    [Fact]
    public void TestDropSamplingDecision()
    {
        this.ValidateShouldSample(SamplingDecision.Drop, SamplingDecision.RecordOnly);
    }

    private static SamplingResult BuildRootSamplingResult(SamplingDecision samplingDecision)
    {
        ActivityTagsCollection? attributes = new ActivityTagsCollection
        {
            { "key", samplingDecision.GetType().Name },
        };
        string traceState = samplingDecision.GetType().Name;
#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
        return new SamplingResult(samplingDecision, attributes, traceState);
#pragma warning restore CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
    }

    private void ValidateShouldSample(
        SamplingDecision rootDecision, SamplingDecision expectedDecision)
    {
        var mockSampler = new Mock<Sampler>();
        var sampler = AlwaysRecordSampler.Create(mockSampler.Object);

        SamplingResult rootResult = BuildRootSamplingResult(rootDecision);
        SamplingParameters samplingParameters = new SamplingParameters(
            default, default, "name", ActivityKind.Client, new ActivityTagsCollection(), new List<ActivityLink>());

        mockSampler.Setup(_ => _.ShouldSample(samplingParameters)).Returns(rootResult);

        SamplingResult actualResult = sampler.ShouldSample(samplingParameters);

        if (rootDecision.Equals(expectedDecision))
        {
            Assert.True(actualResult.Equals(rootResult));
            Assert.True(actualResult.Decision.Equals(rootDecision));
        }
        else
        {
            Assert.False(actualResult.Equals(rootResult));
            Assert.True(actualResult.Decision.Equals(expectedDecision));
        }

        Assert.Equal(rootResult.Attributes, actualResult.Attributes);
        Assert.Equal(rootDecision.GetType().Name, actualResult.TraceStateString);
    }
}

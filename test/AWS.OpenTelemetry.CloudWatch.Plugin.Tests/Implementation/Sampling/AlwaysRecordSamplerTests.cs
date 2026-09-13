// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.Sampling;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Tests.Implementation.Sampling;

[Collection(SpanMetricsTestsCollection.Name)]
public class AlwaysRecordSamplerTests
{
    [Fact]
    public void AlwaysRecordSamplerRejectsNullRootSampler()
    {
        Assert.Throws<ArgumentNullException>(() => AlwaysRecordSampler.Create(null!));
    }

    [Fact]
    public void AlwaysRecordSamplerConvertsDropWithoutAttributes()
    {
        var sampler = AlwaysRecordSampler.Create(
            new FixedSampler(new SamplingResult(SamplingDecision.Drop)));

        var result = sampler.ShouldSample(CreateParameters());

        Assert.Equal(SamplingDecision.RecordOnly, result.Decision);
        Assert.Empty(result.Attributes);
        Assert.Null(result.TraceStateString);
    }

    [Fact]
    public void AlwaysRecordSamplerConvertsDropAndPreservesSamplerResult()
    {
        var rootResult = new SamplingResult(
            SamplingDecision.Drop,
            new Dictionary<string, object>
            {
                ["shared"] = "sampler",
                ["sampler-only"] = "yes",
            },
            "vendor=value");
        var sampler = AlwaysRecordSampler.Create(new FixedSampler(rootResult));
        var parameters = CreateParameters(
            new Dictionary<string, object?>
            {
                ["shared"] = "activity",
                ["activity-only"] = "yes",
            });

        var result = sampler.ShouldSample(parameters);
        var attributes = result.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("AlwaysRecordSampler{FixedSampler}", sampler.Description);
        Assert.Equal(SamplingDecision.RecordOnly, result.Decision);
        Assert.Equal("vendor=value", result.TraceStateString);
        Assert.Equal("sampler", attributes["shared"]);
        Assert.Equal("yes", attributes["sampler-only"]);
        Assert.DoesNotContain("activity-only", attributes.Keys);
    }

    [Theory]
    [InlineData(SamplingDecision.RecordOnly)]
    [InlineData(SamplingDecision.RecordAndSample)]
    public void AlwaysRecordSamplerPreservesNonDropResults(SamplingDecision decision)
    {
        var expected = new SamplingResult(decision, "vendor=value");
        var sampler = AlwaysRecordSampler.Create(new FixedSampler(expected));

        var actual = sampler.ShouldSample(CreateParameters());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AlwaysRecordSamplerPreservesInitialAndSamplerTags()
    {
        var rootResult = new SamplingResult(
            SamplingDecision.Drop,
            new Dictionary<string, object>
            {
                ["shared"] = "sampler",
                ["sampler-only"] = "yes",
            },
            "vendor=value");
        var sourceName = "always-record-sampler-" + Guid.NewGuid().ToString("N");
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(AlwaysRecordSampler.Create(new FixedSampler(rootResult)))
            .Build();
        using var source = new ActivitySource(sourceName);
        var initialTags = new ActivityTagsCollection
        {
            { "shared", "activity" },
            { "activity-only", "yes" },
        };

        using var activity = source.StartActivity(
            "operation",
            ActivityKind.Client,
            default(ActivityContext),
            initialTags);

        Assert.NotNull(activity);
        Assert.False(activity.Recorded);
        Assert.Equal("activity", activity.GetTagItem("shared"));
        Assert.Equal("yes", activity.GetTagItem("activity-only"));
        Assert.Equal("yes", activity.GetTagItem("sampler-only"));
        Assert.Equal("vendor=value", activity.TraceStateString);
    }

    private static SamplingParameters CreateParameters(IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        return new SamplingParameters(
            default,
            ActivityTraceId.CreateRandom(),
            "operation",
            ActivityKind.Client,
            tags,
            links: null);
    }

    private sealed class FixedSampler : Sampler
    {
        private readonly SamplingResult result;

        public FixedSampler(SamplingResult result)
        {
            this.result = result;
            this.Description = nameof(FixedSampler);
        }

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            return this.result;
        }
    }
}

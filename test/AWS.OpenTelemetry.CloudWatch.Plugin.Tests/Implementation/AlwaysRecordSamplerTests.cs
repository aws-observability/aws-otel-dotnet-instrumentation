// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests.Implementation;

[Collection(SpanMetricsConnectorCollection.Name)]
public class AlwaysRecordSamplerTests
{
    [Fact]
    public void SpanMetricsAlwaysRecordSamplerRejectsNullRootSampler()
    {
        Assert.Throws<ArgumentNullException>(() => new AlwaysRecordSampler(null!));
    }

    [Fact]
    public void SpanMetricsAlwaysRecordSamplerConvertsDropAndPreservesSamplerResult()
    {
        var rootResult = new SamplingResult(
            SamplingDecision.Drop,
            new Dictionary<string, object>
            {
                ["shared"] = "sampler",
                ["sampler-only"] = "yes",
            },
            "vendor=value");
        var sampler = new AlwaysRecordSampler(new FixedSampler(rootResult));
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
    public void SpanMetricsAlwaysRecordSamplerPreservesNonDropResults(SamplingDecision decision)
    {
        var expected = new SamplingResult(decision, "vendor=value");
        var sampler = new AlwaysRecordSampler(new FixedSampler(expected));

        var actual = sampler.ShouldSample(CreateParameters());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SpanMetricsAlwaysRecordSamplerCanBeDisabled()
    {
        var expected = new SamplingResult(SamplingDecision.Drop);
        var sampler = new AlwaysRecordSampler(new FixedSampler(expected));
        sampler.Enabled = false;

        var actual = sampler.ShouldSample(CreateParameters());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SpanMetricsAlwaysRecordSamplerPreservesInitialActivityTags()
    {
        var rootResult = new SamplingResult(
            SamplingDecision.Drop,
            new Dictionary<string, object>
            {
                ["sampler-only"] = "yes",
            },
            "vendor=value");
        var sourceName = "always-record-sampler-" + Guid.NewGuid().ToString("N");
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysRecordSampler(new FixedSampler(rootResult)))
            .Build();
        using var source = new ActivitySource(sourceName);
        var initialTags = new ActivityTagsCollection
        {
            { "activity-only", "yes" },
        };

        using var activity = source.StartActivity(
            "operation",
            ActivityKind.Client,
            default(ActivityContext),
            initialTags);

        Assert.NotNull(activity);
        Assert.False(activity.Recorded);
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

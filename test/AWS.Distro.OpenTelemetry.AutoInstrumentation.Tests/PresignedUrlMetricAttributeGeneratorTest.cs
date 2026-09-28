// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation;
using OpenTelemetry.Resources;
using Xunit;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributeGenerator;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Generator-level wiring tests for presigned AWS URL attribution.
//
// These exercise AwsMetricAttributeGenerator end to end (config gating, AWS-SDK exclusion, remote
// resource reuse, and suppression of the generic HTTP operation fallback), complementing the
// component tests for the parser and the S3 attributor.
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
#pragma warning disable CS8602 // Dereference of a possibly null reference.
public class PresignedUrlMetricAttributeGeneratorTest : IDisposable
{
    private const string PresignedUrlAttributionEnabledConfig = "OTEL_AWS_APPLICATION_SIGNALS_PRESIGNED_URL_ATTRIBUTION_ENABLED";

    private readonly ActivitySource testSource = new ActivitySource("Test Source");
    private readonly AwsMetricAttributeGenerator generator = new AwsMetricAttributeGenerator();
    private readonly Resource resource = Resource.Empty;
    private readonly Activity? parentSpan;

    public PresignedUrlMetricAttributeGeneratorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        this.parentSpan = this.testSource.StartActivity("test");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, null);
        this.parentSpan?.Dispose();
        this.testSource.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TestPresignedS3AttributionDisabledByDefault()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, null);
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "PUT");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);

        // The generic HTTP fallback derives the remote service from the URL host and appends the
        // port (443 for HTTPS).
        Assert.Equal("example-bucket.s3.us-west-2.amazonaws.com:443", attributes[AttributeAWSRemoteService]);
        Assert.Equal("PUT /object", attributes[AttributeAWSRemoteOperation]);
        Assert.False(attributes.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(attributes.ContainsKey(AttributeAWSRemoteResourceIdentifier));
    }

    [Fact]
    public void TestPresignedS3UrlAttributes()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "GET");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("AWS::S3", attributes[AttributeAWSRemoteService]);
        Assert.Equal("GetObject", attributes[AttributeAWSRemoteOperation]);
        Assert.Equal("AWS::S3::Bucket", attributes[AttributeAWSRemoteResourceType]);
        Assert.Equal("example-bucket", attributes[AttributeAWSRemoteResourceIdentifier]);
    }

    [Fact]
    public void TestPresignedS3UrlUnknownOperationDoesNotFallBackToHttpPath()
    {
        // Bucket-level GET (no object key, no list-type) is ambiguous, so the resolver returns
        // UnknownRemoteOperation. The generic HTTP operation fallback must not overwrite it with a
        // high-cardinality "GET /..." value.
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/"));
        span.SetTag(AttributeHttpRequestMethod, "GET");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("AWS::S3", attributes[AttributeAWSRemoteService]);
        Assert.Equal(UnknownRemoteOperation, attributes[AttributeAWSRemoteOperation]);
        Assert.Equal("AWS::S3::Bucket", attributes[AttributeAWSRemoteResourceType]);
        Assert.Equal("example-bucket", attributes[AttributeAWSRemoteResourceIdentifier]);
    }

    [Fact]
    public void TestPresignedS3UrlUsesLegacyHttpUrlFallback()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeHttpUrl, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpMethod, "HEAD");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("AWS::S3", attributes[AttributeAWSRemoteService]);
        Assert.Equal("HeadObject", attributes[AttributeAWSRemoteOperation]);
        Assert.Equal("AWS::S3::Bucket", attributes[AttributeAWSRemoteResourceType]);
        Assert.Equal("example-bucket", attributes[AttributeAWSRemoteResourceIdentifier]);
    }

    [Fact]
    public void TestPresignedS3UrlExplicitRemoteAttributesWin()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "PUT");
        span.SetTag(AttributeAWSRemoteService, "AWS remote service");
        span.SetTag(AttributeAWSRemoteOperation, "AWS remote operation");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("AWS remote service", attributes[AttributeAWSRemoteService]);
        Assert.Equal("AWS remote operation", attributes[AttributeAWSRemoteOperation]);
    }

    [Fact]
    public void TestPresignedS3UrlDoesNotAttributeAwsSdkSpan()
    {
        // An AWS SDK span (rpc.system=aws-api) must be excluded from presigned attribution even when
        // its rpc.service/rpc.method are absent, so it keeps the generic HTTP attribution.
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "GET");
        span.SetTag(AttributeRpcSystem, "aws-api");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("example-bucket.s3.us-west-2.amazonaws.com:443", attributes[AttributeAWSRemoteService]);
        Assert.Equal("GET /object", attributes[AttributeAWSRemoteOperation]);
        Assert.False(attributes.ContainsKey(AttributeAWSRemoteResourceType));
    }

    [Fact]
    public void TestPresignedS3UrlPeerServiceOverrideIsUnchanged()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "PUT");
        span.SetTag(AttributePeerService, "PeerService");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("PeerService", attributes[AttributeAWSRemoteService]);
        Assert.Equal("PutObject", attributes[AttributeAWSRemoteOperation]);

        // peer.service overrides the remote service but not the resource, mirroring the SDK path: the
        // S3 bucket resource stays attached even though the service is now the peer value.
        Assert.Equal("AWS::S3::Bucket", attributes[AttributeAWSRemoteResourceType]);
        Assert.Equal("example-bucket", attributes[AttributeAWSRemoteResourceIdentifier]);
    }

    [Fact]
    public void TestNonS3PresignedEndpointIsUnchanged()
    {
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("sqs.us-west-2.amazonaws.com", "/123456789012/example-queue"));
        span.SetTag(AttributeHttpRequestMethod, "GET");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("sqs.us-west-2.amazonaws.com:443", attributes[AttributeAWSRemoteService]);
        Assert.Equal("GET /123456789012", attributes[AttributeAWSRemoteOperation]);
        Assert.False(attributes.ContainsKey(AttributeAWSRemoteResourceType));
    }

    [Fact]
    public void TestPresignedS3UrlWithUnrecognizedEndpointIsUnchanged()
    {
        // An access-point host is not a recognized bucket-bearing S3 endpoint. Attribution fails
        // closed and the span keeps the existing generic HTTP attribution.
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeUrlFull, PresignedUrl("example-bucket.s3-accesspoint.us-west-2.amazonaws.com", "/object"));
        span.SetTag(AttributeHttpRequestMethod, "GET");

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("example-bucket.s3-accesspoint.us-west-2.amazonaws.com:443", attributes[AttributeAWSRemoteService]);
        Assert.Equal("GET /object", attributes[AttributeAWSRemoteOperation]);
        Assert.False(attributes.ContainsKey(AttributeAWSRemoteResourceType));
    }

    [Fact]
    public void TestDbResourceAttributionUnaffectedWhenPresignedAttributionEnabled()
    {
        // Enabling presigned attribution must not shadow DB resource attribution.
        Environment.SetEnvironmentVariable(PresignedUrlAttributionEnabledConfig, "true");
        Activity span = this.CreateClientSpan();
        span.SetTag(AttributeDbSystem, "mysql");
        span.SetTag(AttributeDbName, "db_name");
        span.SetTag(AttributeServerAddress, "abc.com");
        span.SetTag(AttributeServerPort, 3306);

        ActivityTagsCollection attributes = this.DependencyAttributes(span);
        Assert.Equal("DB::Connection", attributes[AttributeAWSRemoteResourceType]);
        Assert.Equal("db_name|abc.com|3306", attributes[AttributeAWSRemoteResourceIdentifier]);
    }

    private Activity CreateClientSpan()
    {
        Activity? span = this.testSource.StartActivity("presigned", ActivityKind.Client);
        Assert.NotNull(span);

        // Non-local-root so the dependency path runs without InternalOperation/LOCAL_ROOT handling.
        span.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        return span;
    }

    private ActivityTagsCollection DependencyAttributes(Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributeMap =
            this.generator.GenerateMetricAttributeMapFromSpan(span, this.resource);
        Assert.True(attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric));
        return dependencyMetric!;
    }

    // A realistic sanitized presigned URL: the agent redacts every query value before attribution
    // runs, so all six SigV4 parameters carry the literal "Redacted".
    private static string PresignedUrl(string host, string path)
    {
        return "https://" + host + path +
            "?X-Amz-Algorithm=Redacted" +
            "&X-Amz-Credential=Redacted" +
            "&X-Amz-Signature=Redacted" +
            "&X-Amz-Date=Redacted" +
            "&X-Amz-Expires=Redacted" +
            "&X-Amz-SignedHeaders=Redacted";
    }
}

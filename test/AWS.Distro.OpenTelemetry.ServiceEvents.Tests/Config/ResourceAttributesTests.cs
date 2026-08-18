// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;

/// <summary>
/// Tests for <see cref="ResourceAttributes" />.
/// </summary>
public class ResourceAttributesTests
{
    [Fact]
    public void Defaults_AllFieldsNull()
    {
        var attrs = new ResourceAttributes();

        attrs.CloudProvider.Should().BeNull();
        attrs.CloudRegion.Should().BeNull();
        attrs.HostId.Should().BeNull();
        attrs.K8sPodName.Should().BeNull();
    }

    [Fact]
    public void IsEmpty_OnDefaults_ReturnsTrue()
    {
        new ResourceAttributes().IsEmpty().Should().BeTrue();
    }

    [Fact]
    public void IsEmpty_WhenAnyFieldSet_ReturnsFalse()
    {
        var attrs = new ResourceAttributes { CloudProvider = "aws" };
        attrs.IsEmpty().Should().BeFalse();
    }

    [Fact]
    public void FromOtelResource_NullResource_ReturnsEmpty()
    {
        ResourceAttributes.FromOtelResource(null).IsEmpty().Should().BeTrue();
    }

    [Fact]
    public void FromOtelResource_ExtractsKnownAttributes()
    {
        var resource = ResourceBuilder.CreateEmpty()
            .AddAttributes(new Dictionary<string, object>
            {
                ["cloud.provider"] = "aws",
                ["cloud.region"] = "us-east-1",
                ["host.id"] = "i-0abc123",
                ["k8s.pod.name"] = "my-pod-xyz",
                ["unknown.attribute"] = "ignored",
            })
            .Build();

        var attrs = ResourceAttributes.FromOtelResource(resource);

        attrs.CloudProvider.Should().Be("aws");
        attrs.CloudRegion.Should().Be("us-east-1");
        attrs.HostId.Should().Be("i-0abc123");
        attrs.K8sPodName.Should().Be("my-pod-xyz");
        attrs.HostType.Should().BeNull();
    }

    [Fact]
    public void FromOtelResource_IgnoresEmptyValues()
    {
        var resource = ResourceBuilder.CreateEmpty()
            .AddAttributes(new Dictionary<string, object>
            {
                ["cloud.provider"] = "aws",
                ["cloud.region"] = "   ",
                ["host.id"] = string.Empty,
            })
            .Build();

        var attrs = ResourceAttributes.FromOtelResource(resource);

        attrs.CloudProvider.Should().Be("aws");
        attrs.CloudRegion.Should().BeNull();
        attrs.HostId.Should().BeNull();
    }

    [Fact]
    public void ToDictionary_OnEmpty_ReturnsEmpty()
    {
        new ResourceAttributes().ToDictionary().Should().BeEmpty();
    }

    [Fact]
    public void ToDictionary_OnlyIncludesNonNullValues()
    {
        var attrs = new ResourceAttributes
        {
            CloudProvider = "aws",
            CloudRegion = "us-east-1",
            HostId = "i-0abc123",
            // Other fields left null
        };

        var dict = attrs.ToDictionary();

        dict.Should().HaveCount(3);
        dict.Should().Contain("cloud.provider", "aws");
        dict.Should().Contain("cloud.region", "us-east-1");
        dict.Should().Contain("host.id", "i-0abc123");
    }

    [Fact]
    public void ToDictionary_UsesOtelDotNotationKeys()
    {
        var attrs = new ResourceAttributes
        {
            CloudAccountId = "123456789012",
            CloudAvailabilityZone = "us-east-1a",
            K8sClusterName = "my-cluster",
        };

        var dict = attrs.ToDictionary();

        dict.Keys.Should().BeEquivalentTo(new[]
        {
            "cloud.account.id",
            "cloud.availability_zone",
            "k8s.cluster.name",
        });
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// AWS platform resource attributes from OTel Resource detectors.
/// </summary>
/// <remarks>
/// <para>
/// Contains a curated set of cloud, host, container, and Kubernetes
/// attributes that provide platform context in ServiceEvents telemetry output.
/// Mirrors the Python distro's <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/models/resource_attributes.py"><c>ResourceAttributes</c></see>
/// dataclass.
/// </para>
/// <para>
/// Serialization uses OTel semantic convention dot-notation keys
/// (e.g., <c>cloud.region</c>) and is sparse — only non-null values are
/// included.
/// </para>
/// </remarks>
public sealed record ResourceAttributes
{
    /// <summary>
    /// Mapping from OTel semantic convention keys to property accessors.
    /// </summary>
    private static readonly (string OtelKey, Func<ResourceAttributes, string?> Getter)[] OtelKeyMap = new (string, Func<ResourceAttributes, string?>)[]
    {
        (ResourceSemanticConventions.AttributeCloudProvider, a => a.CloudProvider),
        (ResourceSemanticConventions.AttributeCloudPlatform, a => a.CloudPlatform),
        (ResourceSemanticConventions.AttributeCloudRegion, a => a.CloudRegion),
        (ResourceSemanticConventions.AttributeCloudAccountId, a => a.CloudAccountId),
        (ResourceSemanticConventions.AttributeCloudAvailabilityZone, a => a.CloudAvailabilityZone),
        (ResourceSemanticConventions.AttributeHostId, a => a.HostId),
        (ResourceSemanticConventions.AttributeHostType, a => a.HostType),
        (ResourceSemanticConventions.AttributeContainerId, a => a.ContainerId),
        (ResourceSemanticConventions.AttributeK8sClusterName, a => a.K8sClusterName),
        (ResourceSemanticConventions.AttributeK8sPodName, a => a.K8sPodName),
        (ResourceSemanticConventions.AttributeK8sNamespaceName, a => a.K8sNamespaceName),
    };

    /// <summary>Gets the cloud provider, e.g. <c>"aws"</c>.</summary>
    public string? CloudProvider { get; init; }

    /// <summary>Gets the cloud platform, e.g. <c>"aws_ec2"</c>, <c>"aws_ecs"</c>, <c>"aws_eks"</c>.</summary>
    public string? CloudPlatform { get; init; }

    /// <summary>Gets the cloud region, e.g. <c>"us-east-1"</c>.</summary>
    public string? CloudRegion { get; init; }

    /// <summary>Gets the cloud account ID, e.g. <c>"123456789012"</c>.</summary>
    public string? CloudAccountId { get; init; }

    /// <summary>Gets the cloud availability zone, e.g. <c>"us-east-1a"</c>.</summary>
    public string? CloudAvailabilityZone { get; init; }

    /// <summary>Gets the host ID (EC2 instance ID, etc.), e.g. <c>"i-0abc123def456"</c>.</summary>
    public string? HostId { get; init; }

    /// <summary>Gets the host type, e.g. <c>"t3.medium"</c>.</summary>
    public string? HostType { get; init; }

    /// <summary>Gets the container ID (Docker / containerd), e.g. <c>"abcdef123..."</c>.</summary>
    public string? ContainerId { get; init; }

    /// <summary>Gets the Kubernetes cluster name, e.g. <c>"my-cluster"</c>.</summary>
    public string? K8sClusterName { get; init; }

    /// <summary>Gets the Kubernetes pod name, e.g. <c>"my-pod-xyz"</c>.</summary>
    public string? K8sPodName { get; init; }

    /// <summary>Gets the Kubernetes namespace name, e.g. <c>"default"</c>.</summary>
    public string? K8sNamespaceName { get; init; }

    /// <summary>
    /// Create a <see cref="ResourceAttributes" /> from an OTel <see cref="Resource" />,
    /// extracting only the curated attribute set.
    /// </summary>
    /// <param name="resource">The OTel resource. Null returns an empty instance.</param>
    /// <returns>A new <see cref="ResourceAttributes" /> populated with detected values.</returns>
    public static ResourceAttributes FromOtelResource(Resource? resource)
    {
        if (resource is null)
        {
            return new ResourceAttributes();
        }

        var attrs = resource.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new ResourceAttributes
        {
            CloudProvider = GetString(attrs, ResourceSemanticConventions.AttributeCloudProvider),
            CloudPlatform = GetString(attrs, ResourceSemanticConventions.AttributeCloudPlatform),
            CloudRegion = GetString(attrs, ResourceSemanticConventions.AttributeCloudRegion),
            CloudAccountId = GetString(attrs, ResourceSemanticConventions.AttributeCloudAccountId),
            CloudAvailabilityZone = GetString(attrs, ResourceSemanticConventions.AttributeCloudAvailabilityZone),
            HostId = GetString(attrs, ResourceSemanticConventions.AttributeHostId),
            HostType = GetString(attrs, ResourceSemanticConventions.AttributeHostType),
            ContainerId = GetString(attrs, ResourceSemanticConventions.AttributeContainerId),
            K8sClusterName = GetString(attrs, ResourceSemanticConventions.AttributeK8sClusterName),
            K8sPodName = GetString(attrs, ResourceSemanticConventions.AttributeK8sPodName),
            K8sNamespaceName = GetString(attrs, ResourceSemanticConventions.AttributeK8sNamespaceName),
        };
    }

    /// <summary>
    /// Serialize to a dictionary using OTel dot-notation keys. Only non-null
    /// values are included.
    /// </summary>
    /// <returns>Sparse dictionary of resource attributes.</returns>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (otelKey, getter) in OtelKeyMap)
        {
            var value = getter(this);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[otelKey] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Indicates whether all attributes are unset.
    /// </summary>
    /// <returns><c>true</c> if no attributes are populated; otherwise, <c>false</c>.</returns>
    public bool IsEmpty()
    {
        foreach (var (_, getter) in OtelKeyMap)
        {
            if (!string.IsNullOrWhiteSpace(getter(this)))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetString(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var s = value.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

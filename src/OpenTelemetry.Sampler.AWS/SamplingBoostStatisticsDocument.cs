// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace OpenTelemetry.Sampler.AWS;

internal sealed class SamplingBoostStatisticsDocument
{
    public SamplingBoostStatisticsDocument(
        string clientId,
        string ruleName,
        string serviceName,
        long totalCount,
        long anomalyCount,
        long sampledAnomalyCount,
        double timestamp)
    {
        this.ClientID = clientId;
        this.RuleName = ruleName;
        this.ServiceName = serviceName;
        this.TotalCount = totalCount;
        this.AnomalyCount = anomalyCount;
        this.SampledAnomalyCount = sampledAnomalyCount;
        this.Timestamp = timestamp;
    }

    [JsonPropertyName("ClientID")]
    public string ClientID { get; set; }

    [JsonPropertyName("RuleName")]
    public string RuleName { get; set; }

    [JsonPropertyName("ServiceName")]
    public string ServiceName { get; set; }

    [JsonPropertyName("Timestamp")]
    public double Timestamp { get; set; }

    [JsonPropertyName("TotalCount")]
    public long TotalCount { get; set; }

    [JsonPropertyName("AnomalyCount")]
    public long AnomalyCount { get; set; }

    [JsonPropertyName("SampledAnomalyCount")]
    public long SampledAnomalyCount { get; set; }
}

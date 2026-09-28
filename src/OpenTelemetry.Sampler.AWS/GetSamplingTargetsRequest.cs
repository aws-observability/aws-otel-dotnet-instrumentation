// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace OpenTelemetry.Sampler.AWS;

internal sealed class GetSamplingTargetsRequest
{
    public GetSamplingTargetsRequest(List<SamplingStatisticsDocument> documents, List<SamplingBoostStatisticsDocument>? boostDocuments = null)
    {
        this.SamplingStatisticsDocuments = documents;
        this.SamplingBoostStatisticsDocuments = boostDocuments;
    }

    [JsonPropertyName("SamplingStatisticsDocuments")]
    public List<SamplingStatisticsDocument> SamplingStatisticsDocuments { get; set; }

    [JsonPropertyName("SamplingBoostStatisticsDocuments")]
    public List<SamplingBoostStatisticsDocument>? SamplingBoostStatisticsDocuments { get; set; }
}

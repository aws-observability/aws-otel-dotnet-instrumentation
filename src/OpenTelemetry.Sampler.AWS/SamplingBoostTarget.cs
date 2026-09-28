// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace OpenTelemetry.Sampler.AWS;

internal sealed class SamplingBoostTarget
{
    [JsonPropertyName("BoostRate")]
    public double BoostRate { get; set; }

    [JsonPropertyName("BoostRateTTL")]
    public double BoostRateTTL { get; set; }
}

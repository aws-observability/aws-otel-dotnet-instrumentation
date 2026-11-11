// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace OpenTelemetry.Sampler.AWS;

internal sealed class SamplingRateBoost
{
    [JsonPropertyName("MaxRate")]
    public double MaxRate { get; set; }

    [JsonPropertyName("CooldownWindowMinutes")]
    public int CooldownWindowMinutes { get; set; }

    [JsonPropertyName("DisableDefaultAnomalyDetection")]
    public bool DisableDefaultAnomalyDetection { get; set; }
}

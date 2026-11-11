// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class AnomalyCondition
{
    public string? ErrorCodeRegex { get; set; }

    public Regex? CompiledErrorCodeRegex { get; set; }

    public List<string>? Operations { get; set; }

    public int? HighLatencyMs { get; set; }

    public UsageType Usage { get; set; } = UsageType.Both;
}

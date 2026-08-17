// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// Non-payload request context for <see cref="IncidentSnapshot"/>. Per spec §5,
/// payload fields (body, query, path, headers, custom context) are no longer
/// captured or emitted — only <c>type</c>, <c>timestamp</c>, and
/// <c>status_code</c> remain.
/// </summary>
public sealed record RequestContext
{
    /// <summary>Gets the context type. Always <c>"http"</c> in v1.</summary>
    public string Type { get; init; } = "http";

    /// <summary>Gets the epoch milliseconds at request entry.</summary>
    public long Timestamp { get; init; }

    /// <summary>Gets the status code, mirroring <see cref="IncidentSnapshot.StatusCode"/>.</summary>
    public int StatusCode { get; init; }
}

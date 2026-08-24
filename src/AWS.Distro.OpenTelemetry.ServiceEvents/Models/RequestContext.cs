// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// Non-payload request context for <see cref="IncidentSnapshot"/>. Payload fields
/// (body, query, path, headers, custom context) are deliberately neither captured nor
/// emitted — only <c>type</c>, <c>timestamp</c> and <c>status_code</c> remain, so a
/// snapshot cannot carry request data off the host.
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

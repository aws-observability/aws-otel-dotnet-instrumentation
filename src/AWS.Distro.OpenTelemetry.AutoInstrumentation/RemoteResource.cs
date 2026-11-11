// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A remote resource (type and identifier) identified from a presigned AWS URL, e.g. an S3 bucket.
/// </summary>
internal sealed class RemoteResource
{
    internal RemoteResource(string type, string identifier)
    {
        this.Type = type;
        this.Identifier = identifier;
    }

    internal string Type { get; }

    internal string Identifier { get; }
}

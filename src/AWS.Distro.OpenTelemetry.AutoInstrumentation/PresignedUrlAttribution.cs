// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// The Application Signals remote attribution derived from a presigned AWS URL. A resource is
/// present only when the service-specific attributor can identify it confidently.
/// </summary>
internal sealed class PresignedUrlAttribution
{
    internal PresignedUrlAttribution(string remoteService, string remoteOperation, RemoteResource? remoteResource)
    {
        this.RemoteService = remoteService;
        this.RemoteOperation = remoteOperation;
        this.RemoteResource = remoteResource;
    }

    internal string RemoteService { get; }

    internal string RemoteOperation { get; }

    internal RemoteResource? RemoteResource { get; }
}

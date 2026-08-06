// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Holds the AWS Distro for OpenTelemetry .NET version string. Exposed so sibling
/// assemblies (e.g. ServiceEvents) can stamp <c>telemetry.distro.version</c> from the
/// same source as the main distro resource (see <c>Plugin.DistroAttributes</c>).
/// </summary>
public static class Version
{
    /// <summary>The distro package version (without the <c>-aws</c> suffix).</summary>
    public static string version = "1.14.0.dev0";
}

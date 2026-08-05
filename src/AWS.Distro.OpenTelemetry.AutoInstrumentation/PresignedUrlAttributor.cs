// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Derives Application Signals attribution from a presigned AWS URL. Parses the span's URL once,
/// then lets each service-specific attributor try to claim it based on the endpoint hostname (the
/// signing service cannot be read from the credential scope because it is redacted). If none claims
/// the URL — custom CNAMEs, unknown endpoints, or non-presigned URLs — attribution falls back to the
/// existing behavior.
/// </summary>
internal sealed class PresignedUrlAttributor
{
    private PresignedUrlAttributor()
    {
    }

    internal static PresignedUrlAttribution? Attribute(Activity span)
    {
        PresignedAwsUrl? presignedAwsUrl = PresignedAwsUrlParser.Parse(span);
        if (presignedAwsUrl == null)
        {
            return null;
        }

        return Attribute(presignedAwsUrl);
    }

    private static PresignedUrlAttribution? Attribute(PresignedAwsUrl presignedAwsUrl)
    {
        // Only S3 is supported today. Additional services (e.g. SQS, execute-api) can be tried here
        // in turn, each claiming the URL only when it recognizes the endpoint.
        return S3PresignedUrlAttributor.Attribute(presignedAwsUrl);
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// How, if at all, collector-less OTLP metrics export should authenticate.
///
/// This is three states rather than a boolean because "do not sign" is not the same as "do not
/// export". Collector-less export requires <c>OTEL_METRICS_EXPORTER=none</c>, which leaves no
/// upstream reader to fall back on, so treating a bearer-token configuration as simply "disabled"
/// exports nothing at all.
/// </summary>
internal enum CollectorlessMetricsAuthMode
{
    /// <summary>
    /// Not a recognized CloudWatch metrics endpoint, or a prerequisite is unmet. No reader is
    /// registered and upstream behavior is left alone.
    /// </summary>
    Disabled,

    /// <summary>
    /// Sign each export with SigV4, service <c>monitoring</c>, using the credential provider chain.
    /// </summary>
    SigV4,

    /// <summary>
    /// The customer configured an explicit <c>Authorization</c> header for metrics, which
    /// CloudWatch Metrics accepts. Export with that header and add no signature.
    /// </summary>
    BearerToken,
}

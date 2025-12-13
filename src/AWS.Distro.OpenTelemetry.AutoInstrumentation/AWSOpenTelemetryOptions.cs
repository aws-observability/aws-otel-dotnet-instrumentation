// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Setting Options for the OpenTelemetry .NET distribution for AWS
/// </summary>
public class AWSOpenTelemetryOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AWSOpenTelemetryOptions"/> class.
    /// </summary>
    public AWSOpenTelemetryOptions()
        : this(new ConfigurationBuilder().Build())
    {
    }

    internal AWSOpenTelemetryOptions(IConfiguration configuration)
    {
        // custom implementation of initializing exporter settings / instrumentations / Service Name etc.
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace CloudWatchPluginOtel;

public sealed class AwsInstrumentationPlugin
{
    public AwsInstrumentationPlugin()
    {
    }

    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        Console.WriteLine("AwsInstrumentationPlugin.BeforeConfigureTracerProvider invoked.");
        return builder.AddAWSInstrumentation();
    }
}

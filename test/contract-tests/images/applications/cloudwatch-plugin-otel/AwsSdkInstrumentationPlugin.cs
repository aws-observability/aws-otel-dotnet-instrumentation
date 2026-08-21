// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace SampleApp;

public sealed class AwsSdkInstrumentationPlugin
{
    public AwsSdkInstrumentationPlugin()
    {
    }

    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        Console.WriteLine("AwsSdkInstrumentationPlugin.BeforeConfigureTracerProvider invoked.");
        return builder.AddAWSInstrumentation();
    }
}

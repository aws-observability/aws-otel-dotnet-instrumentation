// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Amazon.Runtime.Internal;

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

internal class AWSClientsInstrumentation
{
    private static bool? isAwsSdkV4;

    public AWSClientsInstrumentation(AWSClientInstrumentationOptions options)
    {
        // Check if AWS SDK v4 is being used and skip instrumentation if so
        if (IsAwsSdkV4())
        {
            // Don't register the pipeline customizer for AWS SDK v4 due to compatibility issues
            return;
        }

        RuntimePipelineCustomizerRegistry.Instance.Register(new AWSTracingPipelineCustomizer(options));
    }

    private static bool IsAwsSdkV4()
    {
        if (isAwsSdkV4.HasValue)
        {
            return isAwsSdkV4.Value;
        }

        try
        {
            // Try to detect AWS SDK v4 by checking for the absence of ImmutableCredentials type
            // which was removed in v4, or by checking assembly version
            var awsCoreAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "AWSSDK.Core");

            if (awsCoreAssembly != null)
            {
                var version = awsCoreAssembly.GetName().Version;
                if (version != null && version.Major >= 4)
                {
                    isAwsSdkV4 = true;
                    return true;
                }

                // Additional check: Look for ImmutableCredentials type which was removed in v4
                var immutableCredentialsType = awsCoreAssembly.GetType("Amazon.Runtime.ImmutableCredentials");
                if (immutableCredentialsType == null)
                {
                    isAwsSdkV4 = true;
                    return true;
                }
            }

            // If we can't find AWSSDK.Core or it's v3, assume v3
            isAwsSdkV4 = false;
            return false;
        }
        catch (Exception)
        {
            // If detection fails, assume v3 for compatibility and allow instrumentation
            isAwsSdkV4 = false;
            return false;
        }
    }
}

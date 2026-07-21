// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0
//
// Minimal .NET Framework (net472) app that uses AWS SDK for .NET v3 to make a real
// S3 call (GetBucketLocation), mirroring the aws-application-signals-test-framework
// sigv4 sample app. Purpose: observe what happens when ADOT v4 instrumentation is
// injected into a v3-SDK app on .NET Framework.
//
// We care about distinguishing two outcomes:
//   (1) APP BREAKS   -> the AWS SDK call itself throws MissingMethodException /
//                       TypeLoadException / assembly-binding failure (i.e. our v4
//                       AWSSDK.Core in the GAC got bound over the app's v3).
//   (2) SILENT LOSS  -> the AWS SDK call SUCCEEDS, app runs fine, only the AWS SDK
//                       span is missing (instrumentation couldn't hook the v3 pipeline).
//
// The app prints, on every call:
//   - the loaded AWSSDK.Core assembly version actually in the AppDomain (3.x vs 4.x)
//   - whether the S3 call succeeded or threw, with the exception type
// so a human reading stdout can classify the outcome immediately.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace AwsV3ReproApp
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Optional: bucket name + region can be passed as args; defaults are safe read-only.
            string bucketName = args.Length > 0 ? args[0] : "amazon-otel-repro-does-not-need-to-exist";
            string regionName = args.Length > 1 ? args[1] : "us-west-2";

            PrintLoadedAwsSdkCoreVersion("startup");

            // Loop a few times so an auto-instrumentation profiler (which attaches at process
            // start) has spans to emit, and so transient issues are visible across iterations.
            int iterations = 5;
            int failures = 0;
            for (int i = 1; i <= iterations; i++)
            {
                Console.WriteLine($"\n=== iteration {i}/{iterations} ===");
                try
                {
                    using (var s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(regionName)))
                    {
                        var req = new GetBucketLocationRequest { BucketName = bucketName };
                        // Synchronous wait so the console output is ordered and any exception surfaces here.
                        GetBucketLocationResponse resp = s3.GetBucketLocationAsync(req)
                            .GetAwaiter().GetResult();
                        Console.WriteLine($"[OK] GetBucketLocation returned HTTP {(int)resp.HttpStatusCode} " +
                                          $"location='{resp.Location}'");
                    }
                }
                catch (AmazonS3Exception ex)
                {
                    // An AWS *service* error (e.g. bucket not found / access denied) still means the
                    // SDK CALL PATH WORKED end-to-end -> this is NOT an app break. Classify as OK-ish.
                    Console.WriteLine($"[SERVICE-ERROR, app path OK] {ex.GetType().Name}: " +
                                      $"{ex.StatusCode} {ex.ErrorCode} - {ex.Message}");
                }
                catch (MissingMethodException ex)
                {
                    failures++;
                    Console.WriteLine($"[APP BREAK] MissingMethodException — v3/v4 binary incompatibility: {ex.Message}");
                }
                catch (TypeLoadException ex)
                {
                    failures++;
                    Console.WriteLine($"[APP BREAK] TypeLoadException — v3/v4 binary incompatibility: {ex.Message}");
                }
                catch (FileLoadException ex)
                {
                    failures++;
                    Console.WriteLine($"[APP BREAK] FileLoadException — assembly binding conflict: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Any other exception: print full type + stack so we can tell if it originates in
                    // AWS SDK binding vs. ordinary network/credential errors.
                    Console.WriteLine($"[OTHER EXCEPTION] {ex.GetType().FullName}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    // Network/credential errors are expected if the box has no creds/egress; those are
                    // NOT app breaks. We only count the binding exceptions above as breaks.
                }

                Thread.Sleep(1000);
            }

            PrintLoadedAwsSdkCoreVersion("shutdown");

            Console.WriteLine($"\n=== SUMMARY: {failures} binding/app-break failures out of {iterations} iterations ===");
            Console.WriteLine(failures == 0
                ? "RESULT: app did NOT break (outcome = silent-telemetry-loss class, if spans are absent)."
                : "RESULT: app BROKE (binding exception in the customer's own AWS SDK call path).");

            // Give the profiler's exporter a moment to flush before exit.
            Thread.Sleep(3000);
            return failures == 0 ? 0 : 1;
        }

        private static void PrintLoadedAwsSdkCoreVersion(string phase)
        {
            var core = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "AWSSDK.Core");
            if (core == null)
            {
                Console.WriteLine($"[{phase}] AWSSDK.Core not yet loaded into the AppDomain.");
                return;
            }

            var name = core.GetName();
            bool hasImmutableCreds = core.GetType("Amazon.Runtime.ImmutableCredentials") != null;
            Console.WriteLine($"[{phase}] Loaded AWSSDK.Core: version={name.Version} " +
                              $"location='{core.Location}' " +
                              $"(ImmutableCredentials type present={hasImmutableCreds}) " +
                              $"=> {(name.Version.Major >= 4 ? "v4 BOUND" : "v3 bound")}");
        }
    }
}

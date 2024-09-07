// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;

namespace integration_test_app;

public class Program
{
    public static void Main(string[] args)
    {
        // Set the minimum number of worker and I/O completion threads
        ThreadPool.SetMinThreads(workerThreads: 100, completionPortThreads: 100);

        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
                webBuilder.ConfigureKestrel(serverOptions =>
                    {
                        serverOptions.Limits.MaxConcurrentConnections = 10000;
                        serverOptions.Limits.MaxConcurrentUpgradedConnections = 10000;
                        serverOptions.Limits.MaxRequestBodySize = 52428800;

                    });
            });
}

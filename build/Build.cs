// Copyright Splunk Inc
// SPDX-License-Identifier: Apache-2.0
// Modifications Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

using System.IO.Compression;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

internal partial class Build : NukeBuild
{
    private const string OpenTelemetryAutoInstrumentationDefaultVersion = "v1.9.0";
    private static readonly AbsolutePath TestNuGetPackageApps = NukeBuild.RootDirectory / "test" / "test-applications" / "nuget-package";

    [Solution("AWS.Distro.OpenTelemetry.AutoInstrumentation.sln")]
    private readonly Solution solution;

    public static int Main() => Execute<Build>(x => x.Workflow);

    [Parameter("Configuration to build - Default is 'Release'")]
    private readonly Configuration configuration = Configuration.Release;

    [Parameter($"OpenTelemetry AutoInstrumentation dependency version - Default is '{OpenTelemetryAutoInstrumentationDefaultVersion}'")]
    private readonly string openTelemetryAutoInstrumentationVersion = OpenTelemetryAutoInstrumentationDefaultVersion;

    private readonly AbsolutePath openTelemetryDistributionFolder = RootDirectory / "OpenTelemetryDistribution";

    private IEnumerable<Project> AllProjectsExceptNuGetTestApps() => this.solution.AllProjects.Where(project => !TestNuGetPackageApps.Contains(project.Directory));

    private Target Clean => _ => _
        .Executes(() =>
        {
            DotNetClean();
            this.installationScriptsFolder.DeleteDirectory();
            this.openTelemetryDistributionFolder.DeleteDirectory();
            (RootDirectory / GetOTelAutoInstrumentationFileName()).DeleteDirectory();
        });

    private Target Restore => _ => _
        .After(this.Clean)
        .Executes(() =>
        {
            foreach (var project in this.AllProjectsExceptNuGetTestApps())
            {
                DotNetRestore(s => s
                    .SetProjectFile(project));
            }
        });

    private Target DownloadAutoInstrumentationDistribution => _ => _
        .Executes(async () =>
        {
            var fileName = GetOTelAutoInstrumentationFileName();

            var uri =
                $"https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/{this.openTelemetryAutoInstrumentationVersion}/{fileName}";

            await HttpTasks.HttpDownloadFileAsync(uri, RootDirectory / fileName, clientConfigurator: httpClient =>
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3);
                return httpClient;
            });
        });

    private Target UnpackAutoInstrumentationDistribution => _ => _
        .After(this.DownloadAutoInstrumentationDistribution)
        .After(this.Clean)
        .Executes(() =>
        {
            var fileName = GetOTelAutoInstrumentationFileName();
            this.openTelemetryDistributionFolder.DeleteDirectory();
            (RootDirectory / fileName).UnZipTo(this.openTelemetryDistributionFolder);
            (RootDirectory / fileName).DeleteFile();
        });

    private static string GetOTelAutoInstrumentationFileName()
    {
        string fileName;
        switch (EnvironmentInfo.Platform)
        {
            case PlatformFamily.Windows:
                fileName = "opentelemetry-dotnet-instrumentation-windows.zip";
                break;
            case PlatformFamily.Linux:
                var architecture = RuntimeInformation.ProcessArchitecture;
                string architectureSuffix;
                switch (architecture)
                {
                    case Architecture.Arm64:
                        architectureSuffix = "arm64";
                        break;
                    case Architecture.X64:
                        architectureSuffix = "x64";
                        break;
                    default:
                        throw new NotSupportedException("Not supported Linux architecture " + architecture);
                }

                fileName = Environment.GetEnvironmentVariable("IsAlpine") == "true"
                    ? $"opentelemetry-dotnet-instrumentation-linux-musl-{architectureSuffix}.zip"
                    : $"opentelemetry-dotnet-instrumentation-linux-glibc-{architectureSuffix}.zip";
                break;
            case PlatformFamily.OSX:
                fileName = "opentelemetry-dotnet-instrumentation-macos.zip";
                break;
            case PlatformFamily.Unknown:
                throw new NotSupportedException();
            default:
                throw new ArgumentOutOfRangeException();
        }

        return fileName;
    }

    private Target AddAWSPlugins => _ => _
        .After(this.Compile)
        .Executes(() =>
        {
            FileSystemTasks.CopyDirectoryRecursively(
                    RootDirectory / "src" / "AWS.Distro.OpenTelemetry.AutoInstrumentation" / "bin" / this.configuration /
                    "net6.0",
                    this.openTelemetryDistributionFolder / "net",
                    DirectoryExistsPolicy.Merge,
                    FileExistsPolicy.Skip);

            if (EnvironmentInfo.IsWin)
            {
                FileSystemTasks.CopyDirectoryRecursively(
                    RootDirectory / "src" / "AWS.Distro.OpenTelemetry.AutoInstrumentation" / "bin" / this.configuration /
                    "net462",
                    this.openTelemetryDistributionFolder / "netfx",
                    DirectoryExistsPolicy.Merge,
                    FileExistsPolicy.Skip);
            }
        });

    private Target CopyInstrumentScripts => _ => _
        .After(this.AddAWSPlugins)
        .Executes(() =>
        {
            var source = RootDirectory / "instrument.sh";
            var dest = this.openTelemetryDistributionFolder;
            FileSystemTasks.CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);

            var otelInstrumentSource = RootDirectory / "otel-instrument";
            var otelInstrumentDest = this.openTelemetryDistributionFolder;
            FileSystemTasks.CopyFileToDirectory(otelInstrumentSource, otelInstrumentDest, FileExistsPolicy.Overwrite);
        });

    private Target CopyConfiguration => _ => _
        .After(this.AddAWSPlugins)
        .Executes(() =>
        {
            var source = RootDirectory / "src" / "AWS.Distro.OpenTelemetry.AutoInstrumentation" / "configuration";
            var dest = this.openTelemetryDistributionFolder / "configuration";
            FileSystemTasks.CopyDirectoryRecursively(source, dest, DirectoryExistsPolicy.Merge, FileExistsPolicy.Skip);
        });

    private Target ExtendLicenseFile => _ => _
        .After(this.AddAWSPlugins)
        .Executes(() =>
        {
            var licenseFilePath = this.openTelemetryDistributionFolder / "LICENSE";

            var licenseContent = licenseFilePath.ReadAllText();

            var additionalOTelNetAutoInstrumentationContent = @"
Libraries

- OpenTelemetry.AutoInstrumentation.Native
- OpenTelemetry.AutoInstrumentation.AspNetCoreBootstrapper
- OpenTelemetry.AutoInstrumentation.Loader,
- OpenTelemetry.AutoInstrumentation.StartupHook,
- OpenTelemetry.AutoInstrumentation,
are under the following copyright:
Copyright The OpenTelemetry Authors under Apache License Version 2.0
(<https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/main/LICENSE>).
";

            if (!licenseContent.Contains(additionalOTelNetAutoInstrumentationContent))
            {
                licenseFilePath.WriteAllText(licenseContent + additionalOTelNetAutoInstrumentationContent);
            }
        });

    private Target PackAWSDistribution => _ => _
        .After(this.CopyInstrumentScripts)
        .After(this.CopyConfiguration)
        .After(this.ExtendLicenseFile)
        .Executes(() =>
        {
            var fileName = GetOTelAutoInstrumentationFileName();
            this.openTelemetryDistributionFolder.ZipTo(RootDirectory / "bin" / ("aws-distro-" + fileName), compressionLevel: CompressionLevel.SmallestSize, fileMode: FileMode.Create);
        });

    private Target Compile => _ => _
        .After(this.Restore)
        .After(this.UnpackAutoInstrumentationDistribution)
        .Executes(() =>
        {
            foreach (var project in this.AllProjectsExceptNuGetTestApps())
            {
                DotNetBuild(s => s
                    .SetProjectFile(project)
                    .SetNoRestore(true)
                    .SetConfiguration(this.configuration));
            }
        });

#pragma warning disable SA1400 // Access modifier should be declared
    Target RunUnitTests => _ => _
        .After(this.Compile)
        .Executes(() =>
        {
            var project = this.solution.AllProjects.First(project => project.Name == "AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests");

            DotNetTest(s => s
                .SetNoBuild(true)
                .SetProjectFile(project)
                .SetConfiguration(this.configuration));
        });
#pragma warning restore SA1400 // Access modifier should be declared

#pragma warning disable SA1400 // Access modifier should be declared
    Target RunIntegrationTests => _ => _
        .After(this.Compile)
        .After(this.AddAWSPlugins)
        .Executes(() =>
        {
            var project = this.solution.AllProjects.First(project => project.Name == "AWS.Distro.OpenTelemetry.AutoInstrumentation.IntegrationTests");

            DotNetTest(s => s
                .SetNoBuild(true)
                .SetProjectFile(project)
                .SetFilter("Category!=NuGetPackage")
                .SetConfiguration(this.configuration));
        });
#pragma warning restore SA1400 // Access modifier should be declared

    private Target Workflow => _ => _
        .DependsOn(this.Clean)
        .DependsOn(this.Restore)
        .DependsOn(this.BuildInstallationScripts)
        .DependsOn(this.DownloadAutoInstrumentationDistribution)
        .DependsOn(this.UnpackAutoInstrumentationDistribution)
        .DependsOn(this.Compile)
        .DependsOn(this.AddAWSPlugins)
        .DependsOn(this.CopyInstrumentScripts)
        .DependsOn(this.CopyConfiguration)
        .DependsOn(this.ExtendLicenseFile)
        // .DependsOn(RunUnitTests)
        // .DependsOn(RunIntegrationTests)
        .DependsOn(this.PackAWSDistribution);
}

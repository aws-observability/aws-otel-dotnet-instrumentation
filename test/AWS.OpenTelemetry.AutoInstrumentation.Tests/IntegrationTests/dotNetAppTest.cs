// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using FluentAssertions;

using Xunit.Abstractions;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests.IntegrationTests;

/// <summary>
/// Integration Test for AutoInstrumentation for .NET application
/// </summary>
/// <param name="output">ITestOutputHelper instance</param>
/// <param name="awsFixture">AWS Test Fixture defined in AWSCollections</param>
[Collection(AWSCollection.Name)]
public class DotNetAppTest(ITestOutputHelper output, AWSFixture awsFixture)
{
    private readonly AWSFixture aws = awsFixture;
    private ITestOutputHelper output = output;

    /// <summary>
    /// Test auto instrumentation initialization using plugin
    /// </summary>
    [Fact]
    public void TestAWSPluginIntiailization()
    {
        // Enable Plugins
        this.EnablePlugins();
        this.EnableBytecodeInstrumentation();

        // Check if exported items had correct trace format
        var (standardOutput, _, _) = this.RunTestApplication();
        standardOutput.Should().Contain("Amazon.AWS.AWSClientInstrumentation");
    }

    private static string GetTestApplicationFilePath(string applicationName)
    {
        var testApplicationDirectory = Path.Combine("test", applicationName);
        var projectDir = Path.Combine(
            SolutionDirectory.Value,
            testApplicationDirectory,
            "bin",
            "Debug",
            "net8.0",
            $"{applicationName}.dll");
        return projectDir;
    }

    private (string StandardOutput, string ErrorOutput, int ProcessId) RunTestApplication()
    {
        var appProcess = new Process();

        appProcess.StartInfo.FileName = "dotnet";
        appProcess.StartInfo.Arguments = GetTestApplicationFilePath("TestSimpleApp.AWS");
        appProcess.StartInfo.UseShellExecute = false;
        appProcess.StartInfo.CreateNoWindow = true;
        appProcess.StartInfo.RedirectStandardOutput = true;
        appProcess.StartInfo.RedirectStandardError = true;
        appProcess.StartInfo.RedirectStandardInput = false;
        appProcess.StartInfo.StandardOutputEncoding = Encoding.Default;
        appProcess.Start();
        using var helper = new ProcessHelper(appProcess);
        appProcess.WaitForExit();

        appProcess.Should().NotBeNull();

        this.output.WriteLine("ProcessId: " + appProcess.Id);
        this.output.WriteLine("Exit Code: " + appProcess.ExitCode);

        return (helper.StandardOutput, helper.ErrorOutput, appProcess.Id);
    }

    private void EnableBytecodeInstrumentation()
    {
        this.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", "1");
    }

    private void EnablePlugins()
    {
        this.SetEnvironmentVariable("OTEL_DOTNET_AUTO_PLUGINS", "AWS.OpenTelemetry.AutoInstrumentation.Plugin, AWS.OpenTelemetry.AutoInstrumentation");
    }

    private void SetEnvironmentVariable(string key, string? value)
    {
        Environment.SetEnvironmentVariable(key, value);
    }

#pragma warning disable SA1201 // Elements should appear in the correct order
    private static readonly Lazy<string> SolutionDirectory = new (() =>
#pragma warning restore SA1201 // Elements should appear in the correct order
    {
        var startDirectory = Environment.CurrentDirectory;
        var currentDirectory = Directory.GetParent(startDirectory);
        const string searchItem = @"AWS.OpenTelemetry.AutoInstrumentation.sln";

        while (true)
        {
            var slnFile = currentDirectory?.GetFiles(searchItem).SingleOrDefault();

            if (slnFile != null)
            {
                break;
            }

            currentDirectory = currentDirectory?.Parent;

            if (currentDirectory == null || !currentDirectory.Exists)
            {
                throw new Exception($"Unable to find solution directory from: {startDirectory}");
            }
        }

        return currentDirectory!.FullName;
    });
}

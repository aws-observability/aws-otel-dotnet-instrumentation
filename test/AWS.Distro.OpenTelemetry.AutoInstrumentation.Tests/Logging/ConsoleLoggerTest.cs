// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

extern alias DistroAutoInstrumentation;
extern alias XrayUdpExporter;

using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Logging;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public abstract class ConsoleLoggerTest : IDisposable
{
    private const string LogLevelEnvVar = "APPLICATION_SIGNALS_LOG_LEVEL";
    private const string Category = "Test.Category";

    // Every level a caller can pass to ILogger.Log, in ascending severity.
    private static readonly LogLevel[] AllLevels =
    {
        LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical, LogLevel.None,
    };

    private readonly string? originalLogLevelEnv;

    protected ConsoleLoggerTest()
    {
        this.originalLogLevelEnv = Environment.GetEnvironmentVariable(LogLevelEnvVar);
        Environment.SetEnvironmentVariable(LogLevelEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LogLevelEnvVar, this.originalLogLevelEnv);
    }

    // Expected levels are spelled out rather than computed so the test does not mirror the implementation.
    [Theory]
    [InlineData(LogLevel.Trace, new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })]
    [InlineData(LogLevel.Debug, new[] { LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })]
    [InlineData(LogLevel.Information, new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })]
    [InlineData(LogLevel.Warning, new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical })]
    [InlineData(LogLevel.Error, new[] { LogLevel.Error, LogLevel.Critical })]
    [InlineData(LogLevel.Critical, new[] { LogLevel.Critical })]
    [InlineData(LogLevel.None, new LogLevel[0])]
    public void IsEnabledOnlyForLevelsAtOrAboveMinimum(LogLevel minLogLevel, LogLevel[] expectedEnabled)
    {
        var logger = this.CreateLogger(Category, minLogLevel);

        var actualEnabled = AllLevels.Where(logger.IsEnabled).ToArray();

        Assert.Equal(expectedEnabled, actualEnabled);
    }

    [Theory]
    [InlineData(LogLevel.Trace, new[] { "Trace", "Debug", "Info", "Warning", "Error", "Critical" })]
    [InlineData(LogLevel.Debug, new[] { "Debug", "Info", "Warning", "Error", "Critical" })]
    [InlineData(LogLevel.Information, new[] { "Info", "Warning", "Error", "Critical" })]
    [InlineData(LogLevel.Warning, new[] { "Warning", "Error", "Critical" })]
    [InlineData(LogLevel.Error, new[] { "Error", "Critical" })]
    [InlineData(LogLevel.Critical, new[] { "Critical" })]
    [InlineData(LogLevel.None, new string[0])]
    public void LogWritesOnlyMessagesAtOrAboveMinimum(LogLevel minLogLevel, string[] expectedLabels)
    {
        var logger = this.CreateLogger(Category, minLogLevel);

        var lines = CaptureConsoleLines(() =>
        {
            foreach (var level in AllLevels)
            {
                logger.Log(level, "message at {Level}", level);
            }
        });

        Assert.Equal(expectedLabels.Length, lines.Length);
        for (int i = 0; i < expectedLabels.Length; i++)
        {
            Assert.Contains($"[{expectedLabels[i]}]", lines[i]);
        }
    }

    [Theory]
    [InlineData(LogLevel.Trace, "Trace")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, "Info")]
    [InlineData(LogLevel.Warning, "Warning")]
    [InlineData(LogLevel.Error, "Error")]
    [InlineData(LogLevel.Critical, "Critical")]
    public void LogFormatsTimestampLevelCategoryAndMessage(LogLevel level, string expectedLabel)
    {
        var logger = this.CreateLogger(Category, LogLevel.Trace);

        var lines = CaptureConsoleLines(() => logger.Log(level, "processed {Count} spans", 42));

        var line = Assert.Single(lines);
        Assert.Matches(
            $@"^\[\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\] \[{expectedLabel}\] {Category}: processed 42 spans$",
            line);
    }

    [Fact]
    public void LogWritesExceptionAfterMessage()
    {
        var logger = this.CreateLogger(Category, LogLevel.Information);
        var exception = new InvalidOperationException("boom");

        var output = CaptureConsole(() => logger.LogError(exception, "export failed"));

        Assert.Contains($"[Error] {Category}: export failed{Environment.NewLine}{exception}", output);
    }

    [Fact]
    public void LogDoesNotWriteExceptionBelowMinimum()
    {
        var logger = this.CreateLogger(Category, LogLevel.Error);

        var output = CaptureConsole(() => logger.LogWarning(new InvalidOperationException("boom"), "ignored"));

        Assert.Empty(output);
    }

    [Fact]
    public void LogWritesNothingForEmptyMessageWithoutException()
    {
        var logger = this.CreateLogger(Category, LogLevel.Trace);

        var output = CaptureConsole(() => logger.Log(LogLevel.Error, default, "state", null, (_, _) => string.Empty));

        Assert.Empty(output);
    }

    [Fact]
    public void LogWritesExceptionEvenWhenMessageIsEmpty()
    {
        var logger = this.CreateLogger(Category, LogLevel.Trace);
        var exception = new InvalidOperationException("boom");

        var output = CaptureConsole(() => logger.Log(LogLevel.Error, default, "state", exception, (_, _) => string.Empty));

        Assert.Contains($"[Error] {Category}: {Environment.NewLine}{exception}", output);
    }

    [Fact]
    public void LogThrowsWhenFormatterIsNullAndLevelIsEnabled()
    {
        var logger = this.CreateLogger(Category, LogLevel.Information);

        Assert.Throws<ArgumentNullException>(() => logger.Log(LogLevel.Information, default, "state", null, null!));
    }

    [Fact]
    public void BeginScopeReturnsDisposableThatDoesNotAffectLogging()
    {
        var logger = this.CreateLogger(Category, LogLevel.Information);

        var output = CaptureConsole(() =>
        {
            using (var scope = logger.BeginScope("scope"))
            {
                Assert.NotNull(scope);
                logger.LogInformation("inside scope");
            }
        });

        Assert.Contains($"[Info] {Category}: inside scope", output);
    }

    [Fact]
    public void ProviderDefaultsToInformationWhenEnvVarIsUnset()
    {
        using var provider = this.CreateProvider();

        AssertMinimumLevel(provider.CreateLogger(Category), LogLevel.Information);
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    public void ProviderReadsMinimumLevelFromEnvVarCaseInsensitively(string envValue, LogLevel expected)
    {
        Environment.SetEnvironmentVariable(LogLevelEnvVar, envValue);
        using var provider = this.CreateProvider();

        AssertMinimumLevel(provider.CreateLogger(Category), expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("verbose")]
    [InlineData("not-a-level")]
    public void ProviderFallsBackToInformationForUnrecognizedEnvVar(string envValue)
    {
        Environment.SetEnvironmentVariable(LogLevelEnvVar, envValue);
        using var provider = this.CreateProvider();

        AssertMinimumLevel(provider.CreateLogger(Category), LogLevel.Information);
    }

    [Fact]
    public void ProviderCreatesLoggerWithRequestedCategory()
    {
        using var provider = this.CreateProvider();
        var logger = provider.CreateLogger("My.Custom.Category");

        var output = CaptureConsole(() => logger.LogWarning("hello"));

        Assert.Contains("[Warning] My.Custom.Category: hello", output);
    }

    // Production code never uses ConsoleLogger directly; it always goes through
    // LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider())).
    // These tests exercise that exact wiring so the env var is verified end to end.
    [Theory]
    [InlineData("Information", new[] { "Info", "Warning", "Error", "Critical" })]
    [InlineData("Warning", new[] { "Warning", "Error", "Critical" })]
    [InlineData("Error", new[] { "Error", "Critical" })]
    [InlineData("None", new string[0])]
    public void LoggerFactoryWiringHonorsEnvVarLevel(string envValue, string[] expectedLabels)
    {
        Assert.Equal(expectedLabels, this.LogAllLevelsThroughFactory(envValue));
    }

    // Documents current behavior: LoggerFactory applies its own default minimum level (Information) before
    // ConsoleLoggerProvider sees a message, and production never calls SetMinimumLevel, so setting
    // APPLICATION_SIGNALS_LOG_LEVEL to Trace or Debug still only emits Information and above.
    // If production starts calling SetMinimumLevel(LogLevel.Trace), this test will fail: move these cases into
    // LoggerFactoryWiringHonorsEnvVarLevel with their full expected levels.
    [Theory]
    [InlineData("Trace")]
    [InlineData("Debug")]
    public void LoggerFactoryWiringDropsLevelsBelowFactoryDefaultOfInformation(string envValue)
    {
        Assert.Equal(new[] { "Info", "Warning", "Error", "Critical" }, this.LogAllLevelsThroughFactory(envValue));
    }

    protected abstract ILogger CreateLogger(string categoryName, LogLevel minLogLevel);

    protected abstract ILoggerProvider CreateProvider();

    private static void AssertMinimumLevel(ILogger logger, LogLevel expectedMinimum)
    {
        foreach (var level in AllLevels.Where(l => l != LogLevel.None))
        {
            Assert.True(
                logger.IsEnabled(level) == (level >= expectedMinimum),
                $"Expected IsEnabled({level}) to be {level >= expectedMinimum} for minimum level {expectedMinimum}.");
        }

        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    // Builds the logger exactly as production does and returns the level label of each line written
    // when one message is logged at every level.
    private string[] LogAllLevelsThroughFactory(string envValue)
    {
        Environment.SetEnvironmentVariable(LogLevelEnvVar, envValue);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(this.CreateProvider()));
        var logger = factory.CreateLogger(Category);

        var lines = CaptureConsoleLines(() =>
        {
            foreach (var level in AllLevels)
            {
                logger.Log(level, "message at {Level}", level);
            }
        });

        return lines.Select(ExtractLabel).ToArray();
    }

    private static string ExtractLabel(string line)
    {
        // "[timestamp] [Label] Category: message"
        var start = line.IndexOf("] [", StringComparison.Ordinal) + 3;
        return line.Substring(start, line.IndexOf(']', start) - start);
    }

    private static string[] CaptureConsoleLines(Action action) =>
        CaptureConsole(action).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString();
    }
}

// The distro and the Xray.Udp exporter each ship an identical copy of ConsoleLogger/ConsoleLoggerProvider
// (same full type name, different assemblies). Every test in ConsoleLoggerTest runs once against each copy.
[Collection(ConsoleLoggerTestCollection.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public sealed class XrayUdpExporterConsoleLoggerTest : ConsoleLoggerTest
{
    protected override ILogger CreateLogger(string categoryName, LogLevel minLogLevel) =>
        new XrayUdpExporter::AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging.ConsoleLogger(categoryName, minLogLevel);

    protected override ILoggerProvider CreateProvider() =>
        new XrayUdpExporter::AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging.ConsoleLoggerProvider();
}

[Collection(ConsoleLoggerTestCollection.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public sealed class DistroConsoleLoggerTest : ConsoleLoggerTest
{
    protected override ILogger CreateLogger(string categoryName, LogLevel minLogLevel) =>
        new DistroAutoInstrumentation::AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging.ConsoleLogger(categoryName, minLogLevel);

    protected override ILoggerProvider CreateProvider() =>
        new DistroAutoInstrumentation::AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging.ConsoleLoggerProvider();
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

#if !NETFRAMEWORK
using System.Collections.Concurrent;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Flushes logs and metrics before a Lambda invocation returns. Lambda freezes the execution environment
/// once the handler returns, so batched logs and periodic metrics would otherwise be exported during a later
/// invocation or lost. The upstream AWSLambda instrumentation only flushes traces (opentelemetry-dotnet-contrib#5404).
/// </summary>
internal static class LambdaTelemetryFlusher
{
    /// <summary>
    /// Processors the plugin adds to each ILogger pipeline in Lambda. Their ParentProvider is the pipeline's
    /// LoggerProvider, which the auto-instrumentation does not otherwise expose.
    /// </summary>
    internal static readonly ConcurrentBag<BaseProcessor<LogRecord>> LogProcessors = new();

    internal static readonly int FlushTimeoutMilliseconds =
        int.TryParse(Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_AWS_LAMBDA_FLUSH_TIMEOUT"), out var timeout) && timeout > 0
            ? timeout
            : 10000;

    /// <summary>
    /// Runs the invocation, then flushes metrics and logs whether it returned or threw.
    /// </summary>
    internal static async Task<T> InvokeAndFlushAsync<T>(
        Func<Task<T>> invocation,
        MeterProvider? meterProvider,
        IEnumerable<BaseProcessor<LogRecord>> logProcessors,
        int timeoutMilliseconds)
    {
        try
        {
            return await invocation().ConfigureAwait(false);
        }
        finally
        {
            // The metric reader ignores the ForceFlush timeout, so bound the wait here. Signals flush in
            // parallel so one hung endpoint doesn't starve the other. A metrics export cut off by the
            // timeout delays the next invocation's metrics by one invocation.
            var flush = Task.WhenAll(
                Task.Run(() => meterProvider?.ForceFlush(timeoutMilliseconds)),
                Task.Run(() =>
                {
                    foreach (var processor in logProcessors)
                    {
                        (processor.ParentProvider as LoggerProvider)?.ForceFlush(timeoutMilliseconds);
                    }
                }));
            await Task.WhenAny(flush, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
        }
    }
}
#endif

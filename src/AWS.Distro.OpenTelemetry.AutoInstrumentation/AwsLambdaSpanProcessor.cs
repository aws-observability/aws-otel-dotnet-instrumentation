// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Adds Lambda span flags and flushes application logs at invocation completion.
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private const int LogFlushTimeoutMilliseconds = 5000;
    private static readonly ConcurrentDictionary<LoggerProvider, byte> LoggerProviders = new();
#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = new Logging.ConsoleLogger(nameof(AwsLambdaSpanProcessor), LogLevel.Warning);
#pragma warning restore CS0436 // Type conflicts with imported type
    private Activity? lambdaActivity;

    /// <summary>
    /// OnStart caches a reference to the lambda activity if it exists and adds
    /// a flag to signify whether there are multiple server spans or not.
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
        if (activity.Source.Name.Equals("OpenTelemetry.Instrumentation.AWSLambda"))
        {
            this.lambdaActivity = activity;
        }

        if (activity.Source.Name.Equals("Microsoft.AspNetCore") && activity.ParentId != null &&
            (activity.ParentId.Equals(this.lambdaActivity?.SpanId) || activity.ParentId.Equals(this.lambdaActivity?.Id)))
        {
            this.lambdaActivity.SetTag(AttributeAWSTraceLambdaFlagMultipleServer, "true");
        }
    }

    /// <summary>
    /// OnEnd Function
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        // AWSLambdaWrapper force-flushes tracing in its finally block, even for unsampled invocations.
        var timeout = timeoutMilliseconds < 0
            ? LogFlushTimeoutMilliseconds
            : Math.Min(timeoutMilliseconds, LogFlushTimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        foreach (var provider in LoggerProviders.Keys)
        {
            try
            {
                // Wait for delivery before Lambda freezes, sharing one bounded budget across providers.
                var remaining = Math.Max(0, timeout - (int)stopwatch.ElapsedMilliseconds);
                if (!provider.ForceFlush(remaining))
                {
                    Logger.LogWarning("Lambda log force-flush failed or timed out; telemetry may be lost.");
                }
            }
            catch (Exception exception)
            {
                // Logging failures must preserve the handler's result or exception and tracing flush.
                Logger.LogWarning(exception, "Lambda log force-flush failed; telemetry may be lost.");
            }
        }

        return base.OnForceFlush(timeoutMilliseconds);
    }

    /// <summary>
    /// Tracks each application's SDK logger provider after it emits a log.
    /// </summary>
    internal sealed class LogRecordProcessor : BaseProcessor<LogRecord>
    {
        /// <inheritdoc/>
        public override void OnEnd(LogRecord data)
        {
            if (this.ParentProvider is LoggerProvider provider)
            {
                LoggerProviders.TryAdd(provider, 0);
            }
        }

        /// <inheritdoc/>
        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            if (this.ParentProvider is LoggerProvider provider)
            {
                LoggerProviders.TryRemove(provider, out _);
            }

            return base.OnShutdown(timeoutMilliseconds);
        }
    }
}

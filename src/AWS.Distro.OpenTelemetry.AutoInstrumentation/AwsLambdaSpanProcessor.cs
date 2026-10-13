// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Adds Lambda span flags and requests logger provider flushes at invocation completion.
/// </summary>
public class AwsLambdaSpanProcessor : BaseProcessor<Activity>
{
    private static readonly List<WeakReference<BaseProcessor<LogRecord>>> LogRecordProcessors = new();

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

    /// <summary>
    /// Tracks an existing log processor without keeping its logger provider alive.
    /// </summary>
    /// <param name="processor">A processor registered by automatic ILogger instrumentation.</param>
    internal static void TrackLogRecordProcessor(BaseProcessor<LogRecord> processor)
    {
        lock (LogRecordProcessors)
        {
            LogRecordProcessors.Add(new WeakReference<BaseProcessor<LogRecord>>(processor));
        }
    }

    /// <summary>
    /// Gets the existing logger providers used by automatic logging instrumentation.
    /// </summary>
    /// <returns>A snapshot of initialized OpenTelemetry logger providers.</returns>
    internal static IReadOnlyCollection<LoggerProvider> GetLoggerProviders()
    {
        var providers = new HashSet<LoggerProvider>();
        lock (LogRecordProcessors)
        {
            // Walk backward so removing dead references preserves the remaining indices.
            for (int i = LogRecordProcessors.Count - 1; i >= 0; i--)
            {
                if (!LogRecordProcessors[i].TryGetTarget(out var processor))
                {
                    LogRecordProcessors.RemoveAt(i);
                    continue;
                }

                if (processor.ParentProvider is LoggerProvider provider)
                {
                    providers.Add(provider);
                }
            }
        }

        try
        {
            // NLog and log4net share a separate upstream provider.
            var instrumentationType = Type.GetType("OpenTelemetry.AutoInstrumentation.Instrumentation, OpenTelemetry.AutoInstrumentation");
            var providerFactory = instrumentationType?.GetField("LoggerProviderFactory", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) as Lazy<LoggerProvider?>;
            if (providerFactory is { IsValueCreated: true } && providerFactory.Value is { } provider)
            {
                providers.Add(provider);
            }
        }
        catch (Exception)
        {
            // Bridge provider discovery must preserve flushing of the ILogger providers.
        }

        return providers;
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        // AWSLambdaWrapper.OnFunctionStop flushes tracing, which invokes this processor.
        foreach (var loggerProvider in GetLoggerProviders())
        {
            try
            {
                // Zero requests a batch export without waiting for completion or confirming delivery.
                loggerProvider.ForceFlush(0);
            }
            catch (Exception)
            {
                // Optional logging must preserve the invocation's result and tracing flush behavior.
            }
        }

        return base.OnForceFlush(timeoutMilliseconds);
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.AutoInstrumentation.CallTarget;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;

/// <summary>
/// Shared entry/exit pairing, context capture, rate limiting, and enqueuing for all DiIntegration0-9 classes.
/// </summary>
internal static class DiIntegrationHelper
{
    private static volatile InstrumentationRegistry? registry;
    private static CaptureConfiguration defaultLimits = CaptureConfiguration.Default;

    public static CallTargetState OnMethodBegin<TTarget>(TTarget instance, object?[] args)
    {
        // Runs inside the user's woven method: capture must never throw into user code.
        try
        {
            return OnMethodBeginCore(instance, args);
        }
        catch
        {
            return CallTargetState.GetDefault();
        }
    }

    // Non-void methods: profiler weaves OnMethodEnd<TTarget, TReturn> returning CallTargetReturn<TReturn>.
    public static CallTargetReturn<TReturn> OnMethodEnd<TTarget, TReturn>(
        TTarget instance, TReturn returnValue, Exception? exception, in CallTargetState state)
    {
        // Capture must never throw into user code; return value is always passed through.
        try
        {
            EndCore(returnValue, hasReturn: true, exception, in state);
        }
        catch
        {
        }

        return new CallTargetReturn<TReturn>(returnValue);
    }

    // Void methods: profiler resolves the End callback by type identity and requires this non-generic
    // CallTargetReturn overload; without it void targets are rejected and capture nothing (verified E2E).
    public static CallTargetReturn OnMethodEnd<TTarget>(
        TTarget instance, Exception? exception, in CallTargetState state)
    {
        try
        {
            EndCore<object?>(null, hasReturn: false, exception, in state);
        }
        catch
        {
        }

        return CallTargetReturn.GetDefault();
    }

    internal static void Configure(InstrumentationRegistry? registry)
    {
        DiIntegrationHelper.registry = registry;
    }

    /// <summary>
    /// Finds the instrumentation key for a registered config whose type name exactly equals <paramref name="targetTypeFullName"/>.
    /// </summary>
    // Exact match only: a suffix match would collide across namespaces (e.g. A.Svc vs B.Svc) and pick a nondeterministic winner.
    internal static string? MatchKeyByType(string targetTypeFullName, InstrumentationRegistry registry)
    {
        foreach (var reg in registry.GetAll())
        {
            var config = reg.Config;
            if (targetTypeFullName == config.TypeName)
            {
                return config.InstrumentationKey;
            }
        }

        return null;
    }

    internal static StackFrameInfo[] CaptureStackTrace(int maxFrames)
    {
        var trace = new StackTrace(skipFrames: 3, fNeedFileInfo: true);
        return BuildFrames(trace, maxFrames);
    }

    // Uses the exception's own throw-site trace, filtered/capped identically to the entry-time stack.
    internal static StackFrameInfo[] CaptureExceptionStackTrace(Exception exception, int maxFrames)
    {
        var trace = new StackTrace(exception, fNeedFileInfo: true);
        return BuildFrames(trace, maxFrames);
    }

    // Filters internal (agent/runtime) frames out of snapshots and caps the frame count.
    private static StackFrameInfo[] BuildFrames(StackTrace trace, int maxFrames)
    {
        var frames = trace.GetFrames();
        if (frames == null)
        {
            return Array.Empty<StackFrameInfo>();
        }

        var result = new List<StackFrameInfo>();
        foreach (var frame in frames)
        {
            if (result.Count >= maxFrames)
            {
                break;
            }

            var method = frame.GetMethod();
            if (method == null)
            {
                continue;
            }

            var declaringType = method.DeclaringType?.FullName ?? string.Empty;
            if (IsInternalFrame(declaringType))
            {
                continue;
            }

            result.Add(new StackFrameInfo(
                FileName: frame.GetFileName(),
                MethodName: $"{declaringType}.{method.Name}",
                LineNumber: frame.GetFileLineNumber()));
        }

        return result.ToArray();
    }

    private static CallTargetState OnMethodBeginCore<TTarget>(TTarget instance, object?[] args)
    {
        if (registry == null)
        {
            return CallTargetState.GetDefault();
        }

        var instrumentationKey = ResolveInstrumentationKey(instance);
        if (instrumentationKey == null)
        {
            return CallTargetState.GetDefault();
        }

        if (!registry.TryHit(instrumentationKey))
        {
            return CallTargetState.GetDefault();
        }

        var reg = registry.Get(instrumentationKey);
        if (reg == null)
        {
            return CallTargetState.GetDefault();
        }

        var config = reg.Config;
        var limits = config.Capture;

        Dictionary<string, CapturedValue>? capturedArgs = null;
        if (limits.CaptureArguments != null)
        {
            capturedArgs = new Dictionary<string, CapturedValue>();
            for (int i = 0; i < args.Length; i++)
            {
                var name = (limits.CaptureArguments.Length > 0 && i < limits.CaptureArguments.Length)
                    ? limits.CaptureArguments[i]
                    : $"arg{i}";

                // Skip args outside a configured named filter.
                if (limits.CaptureArguments.Length > 0 && i >= limits.CaptureArguments.Length)
                {
                    break;
                }

                capturedArgs[name] = ValueSerializer.Serialize(args[i], limits);
            }
        }

        string? traceId = null, spanId = null;
        var activity = Activity.Current;
        if (activity != null)
        {
            traceId = activity.TraceId.ToHexString();
            spanId = activity.SpanId.ToHexString();
        }

        StackFrameInfo[]? stackTrace = null;
        if (limits.CaptureStackTrace)
        {
            stackTrace = CaptureStackTrace(limits.MaxStackFrames);
        }

        // Stored for pairing with OnMethodEnd.
        var entryData = new PendingEntryData
        {
            InstrumentationKey = instrumentationKey,
            LocationHash = config.LocationHash,
            StartTimestamp = Environment.TickCount64,
            Arguments = capturedArgs,
            TraceId = traceId,
            SpanId = spanId,
            ThreadId = Environment.CurrentManagedThreadId,
            ThreadName = Thread.CurrentThread.Name ?? $"Thread-{Environment.CurrentManagedThreadId}",
            StackTrace = stackTrace,
        };

        DIDataStore.RecordEntry(instrumentationKey, entryData);

        // Profiler CallTargetState is (Activity, object state); stash the key for OnMethodEnd.
        return new CallTargetState(activity, instrumentationKey);
    }

    // Shared end-of-method capture; hasReturn distinguishes a real (possibly null) return from a void method.
    private static void EndCore<TReturn>(TReturn returnValue, bool hasReturn, Exception? exception, in CallTargetState state)
    {
        var instrumentationKey = state.State as string;
        if (instrumentationKey == null)
        {
            return;
        }

        var entryData = DIDataStore.RetrieveEntry(instrumentationKey);
        if (entryData == null)
        {
            return;
        }

        var reg = registry?.Get(instrumentationKey);
        var limits = reg?.Config.Capture ?? defaultLimits;

        CapturedValue? capturedReturn = null;
        if (hasReturn && limits.CaptureReturn)
        {
            capturedReturn = ValueSerializer.Serialize(returnValue, limits);
        }

        // Message truncated to MaxStringLength; exception stack filtered/capped like the entry stack.
        CapturedValue? capturedException = null;
        if (exception != null)
        {
            var message = exception.Message;
            var truncated = message.Length > limits.MaxStringLength;
            if (truncated)
            {
                message = message[..limits.MaxStringLength];
            }

            capturedException = new CapturedValue
            {
                Type = exception.GetType().FullName ?? "System.Exception",
                Value = message,
                Truncated = truncated,
                StackFrames = CaptureExceptionStackTrace(exception, limits.MaxStackFrames),
            };
        }

        var durationMs = Environment.TickCount64 - entryData.StartTimestamp;

        var capture = new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = instrumentationKey,
            LocationHash = entryData.LocationHash,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationMs = durationMs,
            TraceId = entryData.TraceId,
            SpanId = entryData.SpanId,
            ThreadId = entryData.ThreadId,
            ThreadName = entryData.ThreadName,
            Arguments = entryData.Arguments,
            ReturnValue = capturedReturn,
            Exception = capturedException,
            StackTrace = entryData.StackTrace,
        };

        DIDataStore.Enqueue(capture);
    }

    private static string? ResolveInstrumentationKey<TTarget>(TTarget instance)
    {
        if (registry == null)
        {
            return null;
        }

        var targetType = typeof(TTarget).FullName;
        if (targetType == null)
        {
            return null;
        }

        return MatchKeyByType(targetType, registry);
    }

    private static bool IsInternalFrame(string typeName) =>
        typeName.StartsWith("AWS.Distro.OpenTelemetry.DynamicInstrumentation") ||
        typeName.StartsWith("System.Runtime") ||
        typeName.StartsWith("System.Threading");
}

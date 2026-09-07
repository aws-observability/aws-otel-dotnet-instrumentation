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
        // For an awaitable return (Task/Task<T>/ValueTask/ValueTask<T>) the profiler calls this synchronously
        // with the still-incomplete task, THEN calls OnAsyncMethodEnd once it completes. Serializing the task
        // here would capture an incomplete result and could block/deadlock the user thread (accessing .Result),
        // so we defer: leave the paired entry in place and capture in OnAsyncMethodEnd. Mirrors the profiler's
        // own NoCodeIntegrationHelper.OnMethodEnd, which returns early for the same set of awaitable types.
        if (IsAwaitableReturn(typeof(TReturn)))
        {
            return new CallTargetReturn<TReturn>(returnValue);
        }

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

    // Async non-void methods: the profiler awaits the returned Task/ValueTask and calls this with the
    // COMPLETED, unwrapped result (returnValue is T for Task<T>/ValueTask<T>; null object for Task/ValueTask).
    // exception is non-null if the awaited task faulted. This is the profiler's built-in continuation
    // mechanism (IntegrationMapper.CreateAsyncEndMethodDelegate) — no profiler fork required.
    public static TReturn OnAsyncMethodEnd<TTarget, TReturn>(
        TTarget instance, TReturn returnValue, Exception? exception, in CallTargetState state)
    {
        // Capture must never throw into user code; the awaited result is always passed through.
        try
        {
            EndCore(returnValue, hasReturn: true, exception, in state);
        }
        catch
        {
        }

        return returnValue;
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
    /// Finds every instrumentation key for registered configs whose type name exactly equals
    /// <paramref name="targetTypeFullName"/>, via the registry's indexed lookup (O(1), not a scan).
    /// </summary>
    /// <param name="targetTypeFullName">Fully-qualified type name from the woven call.</param>
    /// <param name="registry">The instrumentation registry to resolve against.</param>
    /// <returns>All matching keys, or null when the type is unregistered or ambiguous.</returns>
    // Exact match only: a suffix match would collide across namespaces (e.g. A.Svc vs B.Svc).
    internal static List<string>? MatchKeysByType(string targetTypeFullName, InstrumentationRegistry registry) =>
        registry.ResolveKeysByType(targetTypeFullName);

    internal static StackFrameInfo[] CaptureStackTrace(int maxFrames)
    {
        // No skipFrames count: the agent's own frames are dropped by BuildFrames' IsInternalFrame filter,
        // which is robust where a fixed count would break if the call depth changed.
        var trace = new StackTrace(fNeedFileInfo: true);
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

        var instrumentationKeys = ResolveInstrumentationKeys(instance, args.Length);
        if (instrumentationKeys == null)
        {
            return CallTargetState.GetDefault();
        }

        // The trace context is a property of the CALL, so it is read once and shared; everything else is
        // per-configuration, because each one carries its own capture policy.
        string? traceId = null, spanId = null;
        var activity = Activity.Current;
        if (activity != null)
        {
            traceId = activity.TraceId.ToHexString();
            spanId = activity.SpanId.ToHexString();
        }

        // ONE ENTRY PER CONFIGURATION, each independently rate-limited. TryHit is per config, so a
        // BREAKPOINT that has exhausted its MaxHits stops capturing while a PROBE on the same method
        // continues — which is the whole point of them being separate configurations.
        List<CaptureEntry>? entries = null;
        foreach (var instrumentationKey in instrumentationKeys)
        {
            if (!registry.TryHit(instrumentationKey))
            {
                continue;
            }

            var reg = registry.Get(instrumentationKey);
            if (reg == null)
            {
                continue;
            }

            var entryData = BuildEntryData(instrumentationKey, reg.Config, args, traceId, spanId);
            (entries ??= new List<CaptureEntry>(instrumentationKeys.Count))
                .Add(new CaptureEntry(instrumentationKey, DIDataStore.RecordEntry(entryData)));
        }

        if (entries == null)
        {
            return CallTargetState.GetDefault();
        }

        // Profiler CallTargetState is (Activity, object state); stash the per-config entries so
        // OnMethodEnd pairs with THIS invocation's captures (recursion-safe).
        return new CallTargetState(activity, new CaptureState(entries.ToArray()));
    }

    // Captures the entry-side of one configuration's snapshot: arguments and stack under THAT config's
    // limits, since MaxStringLength/MaxCollectionSize/CaptureStackTrace differ per configuration.
    private static PendingEntryData BuildEntryData(
        string instrumentationKey,
        InstrumentationConfiguration config,
        object?[] args,
        string? traceId,
        string? spanId)
    {
        var limits = config.Capture;

        Dictionary<string, CapturedValue>? capturedArgs = null;
        if (limits.CaptureArguments != null)
        {
            capturedArgs = new Dictionary<string, CapturedValue>();

            // A non-empty CaptureArguments is a positional name filter: capture only the first N args
            // (N = filter length), naming each from the filter. An empty filter captures every arg,
            // naming them arg0, arg1, ... . Bounded by args.Length so a filter longer than the
            // argument list simply captures what exists.
            var hasNameFilter = limits.CaptureArguments.Length > 0;
            var count = hasNameFilter
                ? Math.Min(args.Length, limits.CaptureArguments.Length)
                : args.Length;

            for (int i = 0; i < count; i++)
            {
                var name = hasNameFilter ? limits.CaptureArguments[i] : $"arg{i}";
                capturedArgs[name] = ValueSerializer.Serialize(args[i], limits);
            }
        }

        StackFrameInfo[]? stackTrace = null;
        if (limits.CaptureStackTrace)
        {
            stackTrace = CaptureStackTrace(limits.MaxStackFrames);
        }

        // Stored for pairing with OnMethodEnd.
        return new PendingEntryData
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
    }

    // Shared end-of-method capture; hasReturn distinguishes a real (possibly null) return from a void method.
    private static void EndCore<TReturn>(TReturn returnValue, bool hasReturn, Exception? exception, in CallTargetState state)
    {
        if (state.State is not CaptureState captureState)
        {
            return;
        }

        // One snapshot per configuration that captured on entry. Each is serialized under its OWN limits, so
        // a probe with MaxStringLength=10 and one with 255 both report what they were configured to report.
        foreach (var entry in captureState.Entries)
        {
            EndOne(entry, returnValue, hasReturn, exception);
        }
    }

    private static void EndOne<TReturn>(CaptureEntry entry, TReturn returnValue, bool hasReturn, Exception? exception)
    {
        var instrumentationKey = entry.InstrumentationKey;
        var entryData = DIDataStore.RetrieveEntry(entry.CallId);
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

    // Resolves EVERY config for a woven call. The callback carries no method identity, so we
    // disambiguate co-located methods by arity (the parameter count, = args.Length). Falls back to a
    // type-only match when the arity index has no entry yet — e.g. a capture that fires before the
    // Apply-time IndexArities call, or a registry populated without applying (unit tests).
    private static List<string>? ResolveInstrumentationKeys<TTarget>(TTarget instance, int arity)
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

        var byArity = registry.ResolveKeysByTypeAndArity(targetType, arity);
        if (byArity != null)
        {
            return byArity;
        }

        // NO TYPE-ONLY FALLBACK FOR A TYPE THE PROFILER HAS ALREADY WOVEN.
        //
        // The woven IL outlives its configuration — removal cannot un-weave a method — so a deleted method's
        // callback keeps firing. If the arity index has no entry for this call on a woven type, the config for
        // THIS method is gone, and the only correct answer is to capture nothing. Falling through to the
        // type-only match would attribute the deleted method's arguments and return value to whichever probe
        // still happens to be the type's single remaining config, under ITS LocationHash and capture policy —
        // a plausible-looking snapshot on the wrong probe, with no error reported anywhere.
        //
        // The fallback still serves its documented purpose: a capture that fires between Register and the
        // Apply-time IndexArities call, and registries populated without applying (unit tests). Both are
        // cases where the type has never been woven.
        return registry.HasArityIndex(targetType) ? null : MatchKeysByType(targetType, registry);
    }

    // True when the woven method's return is an awaitable the profiler will continue on (calling
    // OnAsyncMethodEnd with the completed result), so the synchronous OnMethodEnd must defer. Matches the
    // set the profiler's EndMethodHandler recognizes: Task, Task<T>, ValueTask, ValueTask<T>.
    private static bool IsAwaitableReturn(Type returnType)
    {
        if (returnType.IsGenericType)
        {
            if (typeof(Task).IsAssignableFrom(returnType))
            {
                return true; // Task<T>
            }

            var genericDef = returnType.GetGenericTypeDefinition();
            return genericDef == typeof(ValueTask<>);
        }

        return returnType == typeof(Task) || returnType == typeof(ValueTask);
    }

    private static bool IsInternalFrame(string typeName) =>
        typeName.StartsWith("AWS.Distro.OpenTelemetry.DynamicInstrumentation") ||
        typeName.StartsWith("System.Runtime") ||
        typeName.StartsWith("System.Threading");
}

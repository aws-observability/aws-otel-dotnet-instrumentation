// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

/// <summary>
/// A single active instrumentation configuration parsed from the configuration API.
/// </summary>
public sealed class InstrumentationConfiguration
{
    /// <summary>Gets the instrumentation type (probe or breakpoint).</summary>
    public InstrumentationType Type { get; init; }

    /// <summary>Gets the code unit (namespace/module) of the target type.</summary>
    public string CodeUnit { get; init; } = string.Empty;

    /// <summary>Gets the target class name.</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>Gets the target method name.</summary>
    public string MethodName { get; init; } = string.Empty;

    /// <summary>Gets the source file path of the target.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Gets the target line number; 0 for method-level instrumentation.</summary>
    public int LineNumber { get; init; }

    /// <summary>Gets the server-computed hash identifying this location.</summary>
    public string LocationHash { get; init; } = string.Empty;

    /// <summary>Gets the resource ARN, if provided.</summary>
    public string? Arn { get; init; }

    /// <summary>Gets the expiry time, if any.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets the creation time, if provided.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Gets the capture configuration for this instrumentation.</summary>
    public CaptureConfiguration Capture { get; init; } = CaptureConfiguration.Default;

    /// <summary>Gets a value indicating whether this is method-level instrumentation.</summary>
    public bool IsMethodLevel => this.LineNumber == 0;

    /// <summary>Gets a value indicating whether this is line-level instrumentation.</summary>
    public bool IsLineLevel => this.LineNumber > 0;

    /// <summary>Gets the fully-qualified target type name.</summary>
    public string TypeName => $"{this.CodeUnit}.{this.ClassName}";

    /// <summary>Gets the type-and-method key for this target.</summary>
    public string MethodKey => $"{this.TypeName}.{this.MethodName}";

    /// <summary>Gets the unique key for this instrumentation, including line for line-level.</summary>
    public string InstrumentationKey => this.IsLineLevel ? $"{this.MethodKey}:{this.LineNumber}" : this.MethodKey;

    /// <summary>Parses a configuration from an API JSON element.</summary>
    /// <param name="element">The JSON element for a single configuration.</param>
    /// <returns>The parsed configuration, or null if it is malformed or not for .NET.</returns>
    public static InstrumentationConfiguration? Parse(JsonElement element)
    {
        try
        {
            var location = element.GetProperty("Location").GetProperty("CodeLocation");

            var language = location.TryGetProperty("Language", out var langEl)
                ? langEl.GetString() : null;
            if (language != null && !language.Equals("Dotnet", StringComparison.OrdinalIgnoreCase)
                                 && !language.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var typeStr = element.TryGetProperty("InstrumentationType", out var typeEl)
                ? typeEl.GetString() : "PROBE";
            var type = typeStr == "BREAKPOINT" ? InstrumentationType.BREAKPOINT : InstrumentationType.PROBE;

            var codeUnit = location.TryGetProperty(nameof(CodeUnit), out var cuEl) ? cuEl.GetString() ?? string.Empty : string.Empty;
            var className = location.TryGetProperty(nameof(ClassName), out var cnEl) ? cnEl.GetString() ?? string.Empty : string.Empty;
            var methodName = location.TryGetProperty(nameof(MethodName), out var mnEl) ? mnEl.GetString() ?? string.Empty : string.Empty;
            var filePath = location.TryGetProperty(nameof(FilePath), out var fpEl) ? fpEl.GetString() ?? string.Empty : string.Empty;
            var lineNumber = GetIntOrDefault(location, "LineNumber", 0);

            var locationHash = element.TryGetProperty(nameof(LocationHash), out var lhEl)
                ? lhEl.GetString() ?? string.Empty : string.Empty;
            var arn = element.TryGetProperty("ARN", out var arnEl) ? arnEl.GetString() : null;

            DateTimeOffset? expiresAt = null;
            if (element.TryGetProperty(nameof(ExpiresAt), out var expEl))
            {
                expiresAt = ParseTimestamp(expEl);
            }

            DateTimeOffset? createdAt = null;
            if (element.TryGetProperty(nameof(CreatedAt), out var crEl))
            {
                createdAt = ParseTimestamp(crEl);
            }

            var capture = ParseCaptureConfiguration(element, type);

            return new InstrumentationConfiguration
            {
                Type = type,
                CodeUnit = codeUnit,
                ClassName = className,
                MethodName = methodName,
                FilePath = filePath,
                LineNumber = type == InstrumentationType.PROBE ? 0 : lineNumber,
                LocationHash = locationHash,
                Arn = arn,
                ExpiresAt = expiresAt,
                CreatedAt = createdAt,
                Capture = capture,
            };
        }
        catch
        {
            return null;
        }
    }

    private static CaptureConfiguration ParseCaptureConfiguration(JsonElement root, InstrumentationType type)
    {
        if (!root.TryGetProperty("CaptureConfiguration", out var ccEl))
        {
            return CaptureConfiguration.Default;
        }

        if (!ccEl.TryGetProperty("CodeCapture", out var codeCapture))
        {
            return CaptureConfiguration.Default;
        }

        string[]? captureArgs = null;
        if (codeCapture.TryGetProperty("CaptureArguments", out var argsEl))
        {
            captureArgs = argsEl.ValueKind == JsonValueKind.Array
                ? [.. argsEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
                : null;
        }

        string[]? captureLocals = null;
        if (codeCapture.TryGetProperty("CaptureLocals", out var localsEl))
        {
            captureLocals = localsEl.ValueKind == JsonValueKind.Array
                ? [.. localsEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
                : null;
        }

        var captureReturn = GetBoolOrDefault(codeCapture, "CaptureReturn", false);
        var captureStack = GetBoolOrDefault(codeCapture, "CaptureStackTrace", false);

        var maxStringLength = CaptureConfiguration.Default.MaxStringLength;
        var maxCollectionWidth = CaptureConfiguration.Default.MaxCollectionWidth;
        var maxCollectionDepth = CaptureConfiguration.Default.MaxCollectionDepth;
        var maxObjectDepth = CaptureConfiguration.Default.MaxObjectDepth;
        var maxFieldsPerObject = CaptureConfiguration.Default.MaxFieldsPerObject;
        var maxStackFrames = CaptureConfiguration.Default.MaxStackFrames;
        var maxHits = type == InstrumentationType.PROBE ? null : CaptureConfiguration.Default.MaxHits;

        if (codeCapture.TryGetProperty("CaptureLimits", out var limits))
        {
            maxStringLength = CaptureConfiguration.ClampMaxStringLength(
                GetIntOrDefault(limits, "MaxStringLength", maxStringLength));
            maxCollectionWidth = CaptureConfiguration.ClampMaxCollectionWidth(
                GetIntOrDefault(limits, "MaxCollectionWidth", maxCollectionWidth));
            maxCollectionDepth = CaptureConfiguration.ClampMaxCollectionDepth(
                GetIntOrDefault(limits, "MaxCollectionDepth", maxCollectionDepth));
            maxObjectDepth = CaptureConfiguration.ClampMaxObjectDepth(
                GetIntOrDefault(limits, "MaxObjectDepth", maxObjectDepth));
            maxFieldsPerObject = CaptureConfiguration.ClampMaxFieldsPerObject(
                GetIntOrDefault(limits, "MaxFieldsPerObject", maxFieldsPerObject));
            maxStackFrames = CaptureConfiguration.ClampMaxStackFrames(
                GetIntOrDefault(limits, "MaxStackFrames", maxStackFrames));

            if (type == InstrumentationType.BREAKPOINT)
            {
                maxHits = CaptureConfiguration.ClampMaxHits(
                    GetIntOrDefault(limits, "MaxHits", maxHits ?? CaptureConfiguration.Default.MaxHits!.Value));
            }
        }

        return new CaptureConfiguration(captureArgs, captureLocals, captureReturn, captureStack, maxStringLength, maxCollectionWidth, maxCollectionDepth, maxObjectDepth, maxFieldsPerObject, maxStackFrames, maxHits);
    }

    // Safe scalar readers: a mistyped field defaults just that field instead of throwing out
    // of Parse and dropping the whole config. Gate on ValueKind — TryGetInt32 throws on non-numbers.
    private static int GetIntOrDefault(JsonElement obj, string property, int defaultValue)
    {
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return defaultValue;
        }

        return el.TryGetInt32(out var val) ? val : defaultValue;
    }

    private static bool GetBoolOrDefault(JsonElement obj, string property, bool defaultValue)
    {
        if (!obj.TryGetProperty(property, out var el))
        {
            return defaultValue;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        if (el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var dt))
        {
            return dt;
        }

        return null;
    }
}

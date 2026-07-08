// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

public sealed class InstrumentationConfiguration
{
    public InstrumentationType Type { get; init; }
    public string CodeUnit { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int LineNumber { get; init; }
    public string LocationHash { get; init; } = "";
    public string? Arn { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public CaptureConfiguration Capture { get; init; } = CaptureConfiguration.Default;

    public bool IsMethodLevel => LineNumber == 0;
    public bool IsLineLevel => LineNumber > 0;

    public string TypeName => $"{CodeUnit}.{ClassName}";
    public string MethodKey => $"{TypeName}.{MethodName}";
    public string InstrumentationKey => IsLineLevel ? $"{MethodKey}:{LineNumber}" : MethodKey;

    public static InstrumentationConfiguration? Parse(JsonElement element)
    {
        try
        {
            var location = element.GetProperty("Location").GetProperty("CodeLocation");

            var language = location.TryGetProperty("Language", out var langEl)
                ? langEl.GetString() : null;
            if (language != null && !language.Equals("Dotnet", StringComparison.OrdinalIgnoreCase)
                                 && !language.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return null;

            var typeStr = element.TryGetProperty("InstrumentationType", out var typeEl)
                ? typeEl.GetString() : "PROBE";
            var type = typeStr == "BREAKPOINT" ? InstrumentationType.BREAKPOINT : InstrumentationType.PROBE;

            var codeUnit = location.TryGetProperty("CodeUnit", out var cuEl) ? cuEl.GetString() ?? "" : "";
            var className = location.TryGetProperty("ClassName", out var cnEl) ? cnEl.GetString() ?? "" : "";
            var methodName = location.TryGetProperty("MethodName", out var mnEl) ? mnEl.GetString() ?? "" : "";
            var filePath = location.TryGetProperty("FilePath", out var fpEl) ? fpEl.GetString() ?? "" : "";
            var lineNumber = GetIntOrDefault(location, "LineNumber", 0);

            var locationHash = element.TryGetProperty("LocationHash", out var lhEl)
                ? lhEl.GetString() ?? "" : "";
            var arn = element.TryGetProperty("ARN", out var arnEl) ? arnEl.GetString() : null;

            DateTimeOffset? expiresAt = null;
            if (element.TryGetProperty("ExpiresAt", out var expEl))
                expiresAt = ParseTimestamp(expEl);

            DateTimeOffset? createdAt = null;
            if (element.TryGetProperty("CreatedAt", out var crEl))
                createdAt = ParseTimestamp(crEl);

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
                Capture = capture
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
            return CaptureConfiguration.Default;
        if (!ccEl.TryGetProperty("CodeCapture", out var codeCapture))
            return CaptureConfiguration.Default;

        string[]? captureArgs = null;
        if (codeCapture.TryGetProperty("CaptureArguments", out var argsEl))
            captureArgs = argsEl.ValueKind == JsonValueKind.Array
                ? argsEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : null;

        string[]? captureLocals = null;
        if (codeCapture.TryGetProperty("CaptureLocals", out var localsEl))
            captureLocals = localsEl.ValueKind == JsonValueKind.Array
                ? localsEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : null;

        var captureReturn = GetBoolOrDefault(codeCapture, "CaptureReturn", false);
        var captureStack = GetBoolOrDefault(codeCapture, "CaptureStackTrace", false);

        int maxStringLength = CaptureConfiguration.Default.MaxStringLength;
        int maxCollectionWidth = CaptureConfiguration.Default.MaxCollectionWidth;
        int maxCollectionDepth = CaptureConfiguration.Default.MaxCollectionDepth;
        int maxObjectDepth = CaptureConfiguration.Default.MaxObjectDepth;
        int maxFieldsPerObject = CaptureConfiguration.Default.MaxFieldsPerObject;
        int maxStackFrames = CaptureConfiguration.Default.MaxStackFrames;
        int? maxHits = type == InstrumentationType.PROBE ? null : CaptureConfiguration.Default.MaxHits;

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

        return new CaptureConfiguration(
            captureArgs, captureLocals, captureReturn, captureStack,
            maxStringLength, maxCollectionWidth, maxCollectionDepth,
            maxObjectDepth, maxFieldsPerObject, maxStackFrames, maxHits);
    }

    // Safe scalar readers: a mistyped field defaults just that field instead of throwing out
    // of Parse and dropping the whole config. Gate on ValueKind — TryGetInt32 throws on non-numbers.
    private static int GetIntOrDefault(JsonElement obj, string property, int defaultValue)
    {
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number)
            return defaultValue;
        return el.TryGetInt32(out var val) ? val : defaultValue;
    }

    private static bool GetBoolOrDefault(JsonElement obj, string property, bool defaultValue)
    {
        if (!obj.TryGetProperty(property, out var el))
            return defaultValue;
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
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var dt))
            return dt;
        return null;
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

internal static class ValueSerializer
{
    private const string DepthLimitReached = "<depth limit reached>";
    private const string AlreadyCaptured = "<already captured>";

    public static CapturedValue Serialize(object? value, CaptureConfiguration limits, int depth = 0)
        => Serialize(value, limits, objectDepth: depth, collectionDepth: depth, visited: new HashSet<object>(ReferenceEqualityComparer.Instance));

    // Object nesting and collection nesting are bounded independently: a chain of nested
    // objects is limited by MaxObjectDepth, a chain of nested collections/dictionaries by
    // MaxCollectionDepth. Each container checks its own budget before recursing, so cyclic
    // object graphs and self-referencing collections both terminate even without identity tracking.
    // In ADDITION, `visited` tracks reference identity so a value revisited within its own ancestry
    // is reported as AlreadyCaptured before the depth cap is hit (matches Java/JS circular detection).
    private static CapturedValue Serialize(object? value, CaptureConfiguration limits, int objectDepth, int collectionDepth, HashSet<object> visited)
    {
        if (value == null)
        {
            return new CapturedValue { Type = "null", Value = "null" };
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;

        if (IsPrimitive(type))
        {
            return SerializePrimitive(value, typeName);
        }

        if (type == typeof(string))
        {
            return SerializeString((string)value, typeName, limits.MaxStringLength);
        }

        // Reference-cycle detection for reference types only (boxed value types get a fresh box each
        // time, so identity tracking there would never match and could false-positive on interned boxes).
        if (!type.IsValueType && !visited.Add(value))
        {
            return new CapturedValue
            {
                Type = typeName,
                Value = AlreadyCaptured,
                NotCapturedReason = NotCapturedReason.AlreadyCaptured,
            };
        }

        try
        {
            if (value is IDictionary dict)
            {
                if (collectionDepth >= limits.MaxCollectionDepth)
                {
                    return DepthLimited(typeName);
                }

                return SerializeDictionary(dict, typeName, limits, objectDepth, collectionDepth, visited);
            }

            if (value is IEnumerable enumerable && type != typeof(string))
            {
                if (collectionDepth >= limits.MaxCollectionDepth)
                {
                    return DepthLimited(typeName);
                }

                return SerializeCollection(enumerable, typeName, limits, objectDepth, collectionDepth, visited);
            }

            if (objectDepth >= limits.MaxObjectDepth)
            {
                return DepthLimited(typeName);
            }

            return SerializeObject(value, type, typeName, limits, objectDepth, collectionDepth, visited);
        }
        finally
        {
            // Remove on unwind so sibling references to the same object aren't false-positived as
            // cycles — only ancestry (a value nested inside itself) counts as AlreadyCaptured.
            if (!type.IsValueType)
            {
                visited.Remove(value);
            }
        }
    }

    private static CapturedValue DepthLimited(string typeName) =>
        new() { Type = typeName, Value = DepthLimitReached, NotCapturedReason = NotCapturedReason.Depth };

    private static bool IsPrimitive(Type type) =>
        type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTime)
        || type == typeof(DateTimeOffset) || type == typeof(Guid) || type.IsEnum;

    private static CapturedValue SerializePrimitive(object value, string typeName) =>
        new() { Type = typeName, Value = value.ToString() };

    private static CapturedValue SerializeString(string value, string typeName, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return new CapturedValue { Type = typeName, Value = value };
        }

        return new CapturedValue
        {
            Type = typeName,
            Value = value[..maxLength],
            Truncated = true,
        };
    }

    private static CapturedValue SerializeCollection(IEnumerable enumerable, string typeName, CaptureConfiguration limits, int objectDepth, int collectionDepth, HashSet<object> visited)
    {
        // Use ICollection.Count when available to avoid enumerating just to learn the size.
        var knownCount = enumerable switch
        {
            ICollection c => c.Count,
            _ => (int?)null,
        };

        var elements = new List<CapturedValue>();
        int width = limits.MaxCollectionWidth;

        // Pull at most width+1 (width to capture, +1 to detect truncation) — never walk the whole
        // sequence, which may be huge/lazy/infinite and runs on the user's thread.
        foreach (var item in enumerable)
        {
            if (elements.Count >= width)
            {
                knownCount ??= width + 1; // Unknown size: one extra item proves there's more.
                break;
            }

            elements.Add(Serialize(item, limits, objectDepth, collectionDepth + 1, visited));
        }

        var truncated = knownCount is int n && n > width;
        return new CapturedValue
        {
            Type = typeName,
            Elements = elements.ToArray(),
            OriginalSize = truncated ? knownCount : null,
            NotCapturedReason = truncated ? NotCapturedReason.CollectionSize : NotCapturedReason.None,
        };
    }

    private static CapturedValue SerializeDictionary(IDictionary dict, string typeName, CaptureConfiguration limits, int objectDepth, int collectionDepth, HashSet<object> visited)
    {
        var fields = new Dictionary<string, CapturedValue>();
        int count = 0;

        foreach (DictionaryEntry entry in dict)
        {
            if (count >= limits.MaxCollectionWidth)
            {
                break;
            }

            var key = entry.Key?.ToString() ?? "null";
            fields[key] = Serialize(entry.Value, limits, objectDepth, collectionDepth + 1, visited);
            count++;
        }

        var dictTruncated = dict.Count > limits.MaxCollectionWidth;
        return new CapturedValue
        {
            Type = typeName,
            Fields = fields,
            OriginalSize = dictTruncated ? dict.Count : null,
            NotCapturedReason = dictTruncated ? NotCapturedReason.CollectionSize : NotCapturedReason.None,
        };
    }

    private static CapturedValue SerializeObject(object value, Type type, string typeName, CaptureConfiguration limits, int objectDepth, int collectionDepth, HashSet<object> visited)
    {
        var fields = new Dictionary<string, CapturedValue>();
        int count = 0;
        bool fieldCountExceeded = false;

        try
        {
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance;
            foreach (var field in type.GetFields(bindingFlags))
            {
                if (count >= limits.MaxFieldsPerObject)
                {
                    fieldCountExceeded = true;
                    break;
                }

                try
                {
                    var fieldValue = field.GetValue(value);
                    fields[field.Name] = Serialize(fieldValue, limits, objectDepth + 1, collectionDepth, visited);
                    count++;
                }
                catch
                {
                    fields[field.Name] = new CapturedValue { Type = "error", Value = "<access error>" };
                    count++;
                }
            }

            foreach (var prop in type.GetProperties(bindingFlags))
            {
                if (count >= limits.MaxFieldsPerObject)
                {
                    fieldCountExceeded = true;
                    break;
                }

                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                try
                {
                    var propValue = prop.GetValue(value);
                    fields[prop.Name] = Serialize(propValue, limits, objectDepth + 1, collectionDepth, visited);
                    count++;
                }
                catch
                {
                    fields[prop.Name] = new CapturedValue { Type = "error", Value = "<access error>" };
                    count++;
                }
            }
        }
        catch
        {
            return new CapturedValue { Type = typeName, Value = value.ToString() };
        }

        return new CapturedValue
        {
            Type = typeName,
            Fields = fields,
            NotCapturedReason = fieldCountExceeded ? NotCapturedReason.FieldCount : NotCapturedReason.None,
        };
    }
}

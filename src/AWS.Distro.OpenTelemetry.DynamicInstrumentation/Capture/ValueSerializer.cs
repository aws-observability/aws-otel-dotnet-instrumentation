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

            // Only treat as a collection when the size is known in O(1). A countless/lazy IEnumerable is
            // serialized as an object instead — walking it could be unbounded (or infinite) on the user's
            // thread, and reporting a pulled-item count as the size would be a misleading lower bound, not
            // the real length (matches Java/Python). The count comes from non-generic ICollection (arrays,
            // List<T>) or, for sets that implement only the generic interface (HashSet<T>, SortedSet<T>),
            // from ICollection<T>/IReadOnlyCollection<T>.
            if (value is IEnumerable sequence && type != typeof(string) && TryGetKnownCount(value, out var knownCount))
            {
                if (collectionDepth >= limits.MaxCollectionDepth)
                {
                    return DepthLimited(typeName);
                }

                return SerializeCollection(sequence, knownCount, typeName, limits, objectDepth, collectionDepth, visited);
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

    // True only when the element count is available in O(1): non-generic ICollection (arrays, List<T>,
    // Queue<T>, Stack<T>) or the generic ICollection<T>/IReadOnlyCollection<T> that sets implement without
    // the non-generic one (HashSet<T>, SortedSet<T>). A bare IEnumerable has no size and returns false, so
    // it is never walked as a collection. Count is read via the interface's own Count property — no enumeration.
    private static bool TryGetKnownCount(object value, out int count)
    {
        // Any Count getter here belongs to a user type and runs on the user's thread. A throwing or
        // unavailable getter (lazy/remote-backed collections, disposed views, thread-affinity violations)
        // must NOT propagate — it would escape Serialize (which has only a finally, no catch) and abort the
        // whole invocation's capture. On any failure we treat the value as countless and let it fall through
        // to SerializeObject, whose per-field catches contain any further damage.
        try
        {
            if (value is ICollection nonGeneric)
            {
                count = nonGeneric.Count;
                return true;
            }

            foreach (var iface in value.GetType().GetInterfaces())
            {
                if (iface.IsGenericType)
                {
                    var def = iface.GetGenericTypeDefinition();
                    if (def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
                    {
                        count = (int)iface.GetProperty("Count")!.GetValue(value)!;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Fall through to countless.
        }

        count = 0;
        return false;
    }

    private static CapturedValue SerializeCollection(IEnumerable collection, int totalCount, string typeName, CaptureConfiguration limits, int objectDepth, int collectionDepth, HashSet<object> visited)
    {
        // Size is known O(1) (resolved by the caller via TryGetKnownCount), so we capture only the first
        // MaxCollectionWidth items and report the true total — never enumerate past the cap on the user's thread.
        int width = limits.MaxCollectionWidth;

        var elements = new List<CapturedValue>();

        // Enumeration runs on the user's thread over a possibly-live collection. If another thread mutates
        // it mid-walk the enumerator throws (InvalidOperationException: "collection was modified"); keep what
        // was captured so far rather than letting it escape Serialize and abort the whole invocation.
        try
        {
            foreach (var item in collection)
            {
                if (elements.Count >= width)
                {
                    break;
                }

                elements.Add(Serialize(item, limits, objectDepth, collectionDepth + 1, visited));
            }
        }
        catch
        {
            // Partial capture stands.
        }

        var truncated = totalCount > width;
        return new CapturedValue
        {
            Type = typeName,
            Elements = elements.ToArray(),
            OriginalSize = truncated ? totalCount : null,
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

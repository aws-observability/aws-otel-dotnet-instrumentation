// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Telemetry;

/// <summary>
/// Round-trip tests for the hand-rolled OTLP/protobuf encoding in
/// <see cref="ServiceEventsOtlpLogExporter" />. The exporter's <c>AnyValue</c> encoders
/// are exercised and decoded back with a minimal protobuf reader to confirm the wire
/// bytes are valid OTLP (the network path that carries the top-level <c>event_name</c>
/// the Application Signals MCP filters on). Field numbers follow common.proto.
/// </summary>
public class ServiceEventsOtlpLogExporterTests
{
    [Fact]
    public void PrimitiveToAnyValue_EncodesEachScalarType()
    {
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue("hello")).Should().Be("hello");
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue(true)).Should().Be(true);
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue(42)).Should().Be(42L);
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue(9_000_000_000L)).Should().Be(9_000_000_000L);
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue(1.5d)).Should().Be(1.5d);
        AnyValueDecoder.Decode(ServiceEventsOtlpLogExporter.PrimitiveToAnyValue(null)).Should().Be(string.Empty);
    }

    [Fact]
    public void JsonNodeToAnyValue_RoundTripsNestedIncidentBody()
    {
        // A representative IncidentSnapshot body: nested object + array of objects + mixed scalars.
        const string bodyJson = """
        {
          "exception_info": [
            {
              "exception_type": "PetSite.PetSiteDemoFault",
              "call_path": [
                { "function_name": "AdoptionController.CalculateAdoptionFee", "error": true },
                { "function_name": "AdoptionController.TakeMeHome", "error": false }
              ]
            }
          ],
          "request_context": { "type": "http", "timestamp": 1775673990638, "status_code": 500 }
        }
        """;

        var bytes = ServiceEventsOtlpLogExporter.JsonNodeToAnyValue(JsonNode.Parse(bodyJson));
        var decoded = AnyValueDecoder.Decode(bytes);

        var body = decoded.Should().BeOfType<Dictionary<string, object?>>().Subject;

        // request_context: {type, timestamp, status_code}
        var ctx = body["request_context"].Should().BeOfType<Dictionary<string, object?>>().Subject;
        ctx["type"].Should().Be("http");
        ctx["timestamp"].Should().Be(1775673990638L);
        ctx["status_code"].Should().Be(500L);

        // exception_info: [ { exception_type, call_path: [ {function_name, error}, ... ] } ]
        var excInfo = body["exception_info"].Should().BeOfType<List<object?>>().Subject;
        excInfo.Should().HaveCount(1);
        var exc = excInfo[0].Should().BeOfType<Dictionary<string, object?>>().Subject;
        exc["exception_type"].Should().Be("PetSite.PetSiteDemoFault");

        var callPath = exc["call_path"].Should().BeOfType<List<object?>>().Subject;
        callPath.Should().HaveCount(2);
        var frame0 = callPath[0].Should().BeOfType<Dictionary<string, object?>>().Subject;
        frame0["function_name"].Should().Be("AdoptionController.CalculateAdoptionFee");
        frame0["error"].Should().Be(true);
        var frame1 = callPath[1].Should().BeOfType<Dictionary<string, object?>>().Subject;
        frame1["error"].Should().Be(false);
    }
}

/// <summary>
/// Minimal OTLP <c>AnyValue</c> protobuf reader (common.proto): decodes the bytes
/// produced by the exporter back into plain CLR objects for assertions.
/// string_value=1, bool_value=2, int_value=3, double_value=4, array_value=5, kvlist_value=6.
/// </summary>
internal static class AnyValueDecoder
{
    public static object? Decode(byte[] anyValue)
    {
        var pos = 0;
        return ReadAnyValue(anyValue, ref pos, anyValue.Length);
    }

    private static object? ReadAnyValue(byte[] buf, ref int pos, int end)
    {
        // AnyValue is a oneof — take the first (only) field present.
        while (pos < end)
        {
            var (field, wire) = ReadTag(buf, ref pos);
            switch (field)
            {
                case 1: // string_value
                    return ReadString(buf, ref pos);
                case 2: // bool_value
                    return ReadVarint(buf, ref pos) != 0;
                case 3: // int_value
                    return (long)ReadVarint(buf, ref pos);
                case 4: // double_value (fixed64)
                    return BitConverter.Int64BitsToDouble(ReadFixed64(buf, ref pos));
                case 5: // array_value -> ArrayValue
                    return ReadArrayValue(buf, ref pos);
                case 6: // kvlist_value -> KeyValueList
                    return ReadKeyValueList(buf, ref pos);
                default:
                    SkipField(buf, ref pos, wire);
                    break;
            }
        }

        return null;
    }

    private static List<object?> ReadArrayValue(byte[] buf, ref int pos)
    {
        var len = (int)ReadVarint(buf, ref pos);
        var end = pos + len;
        var list = new List<object?>();
        while (pos < end)
        {
            var (field, wire) = ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                var itemLen = (int)ReadVarint(buf, ref pos);
                var itemEnd = pos + itemLen;
                list.Add(ReadAnyValue(buf, ref pos, itemEnd));
                pos = itemEnd;
            }
            else
            {
                SkipField(buf, ref pos, wire);
            }
        }

        return list;
    }

    private static Dictionary<string, object?> ReadKeyValueList(byte[] buf, ref int pos)
    {
        var len = (int)ReadVarint(buf, ref pos);
        var end = pos + len;
        var map = new Dictionary<string, object?>();
        while (pos < end)
        {
            var (field, wire) = ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                var kvLen = (int)ReadVarint(buf, ref pos);
                var kvEnd = pos + kvLen;
                ReadKeyValue(buf, ref pos, kvEnd, map);
                pos = kvEnd;
            }
            else
            {
                SkipField(buf, ref pos, wire);
            }
        }

        return map;
    }

    private static void ReadKeyValue(byte[] buf, ref int pos, int end, Dictionary<string, object?> map)
    {
        string? key = null;
        object? value = null;
        while (pos < end)
        {
            var (field, wire) = ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                key = ReadString(buf, ref pos);
            }
            else if (field == 2 && wire == 2)
            {
                var valLen = (int)ReadVarint(buf, ref pos);
                var valEnd = pos + valLen;
                value = ReadAnyValue(buf, ref pos, valEnd);
                pos = valEnd;
            }
            else
            {
                SkipField(buf, ref pos, wire);
            }
        }

        if (key is not null)
        {
            map[key] = value;
        }
    }

    private static (int Field, int Wire) ReadTag(byte[] buf, ref int pos)
    {
        var tag = ReadVarint(buf, ref pos);
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    private static string ReadString(byte[] buf, ref int pos)
    {
        var len = (int)ReadVarint(buf, ref pos);
        var s = Encoding.UTF8.GetString(buf, pos, len);
        pos += len;
        return s;
    }

    private static ulong ReadVarint(byte[] buf, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static long ReadFixed64(byte[] buf, ref int pos)
    {
        long v = 0;
        for (var i = 0; i < 8; i++)
        {
            v |= (long)buf[pos++] << (8 * i);
        }

        return v;
    }

    private static void SkipField(byte[] buf, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                ReadVarint(buf, ref pos);
                break;
            case 1:
                pos += 8;
                break;
            case 2:
                var len = (int)ReadVarint(buf, ref pos);
                pos += len;
                break;
            case 5:
                pos += 4;
                break;
            default:
                throw new InvalidOperationException($"Unsupported wire type {wire}");
        }
    }
}

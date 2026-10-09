// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

/// <summary>
/// Covers what <c>DISnapshotOtlpEmitter.Create</c> WIRES UP, as opposed to what the pieces do in isolation.
/// </summary>
// WHY THIS EXISTS. The trace-context processor's own tests build their LoggerFactory by hand and register the
// processor themselves, with a comment noting the order matches Create(). That leaves the production wiring
// uncovered: deleting `options.AddProcessor(new SnapshotTraceContextProcessor())` from Create() keeps the whole
// suite green while reintroducing the bug where a snapshot carries the DRAIN THREAD's trace context instead of
// the one captured on the user's thread.
//
// Asserted on the bytes that reach a socket, because that is the only place the exporter's own pipeline is
// observable end to end. A RAW TcpListener rather than an HttpListener or a collector: it accepts the
// connection whatever the protocol, so a wrong transport shows up as the wrong BYTES instead of as a
// connection error that the right transport would produce too.
//
// Serialized with the other suites that touch process-global state: the protocol test below mutates the OTLP
// environment variables, and xunit runs separate collections in parallel.
[Collection("SerialProcessState")]
public class DISnapshotOtlpCreateWiringTests
{
    private const string CapturedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string CapturedSpanId = "00f067aa0ba902b7";

    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string LogsProtocolVar = "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL";

    [Fact]
    public void Create_RegistersTheTraceContextProcessor_SoExportsCarryTheCapturedTraceId()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = new List<byte>();
        var done = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            try
            {
                using var connection = listener.AcceptTcpClient();
                var buffer = new byte[8192];
                var read = connection.GetStream().Read(buffer, 0, buffer.Length);
                received.AddRange(buffer.Take(read));
                RespondOk(connection);
            }
            catch (SocketException)
            {
                // Listener stopped before anything connected; the assertion below reports it.
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true };
        reader.Start();

        try
        {
            var emitter = DISnapshotOtlpEmitter.Create($"http://127.0.0.1:{port}/v1/logs", null);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
                TraceId = CapturedTraceId,
                SpanId = CapturedSpanId,
                TimestampMs = 1_785_000_000_000,
            });

            // Disposing flushes the batch through the exporter, which is what opens the connection.
            emitter.Dispose();

            done.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the exporter must attempt an export");
        }
        finally
        {
            listener.Stop();
        }

        // A trace id travels as 16 raw bytes in the OTLP payload, so match bytes rather than hex text. This
        // relies on the export being OTLP/HTTP, which Create pins — see the protocol tests below.
        IndexOfSequence(received, Convert.FromHexString(CapturedTraceId)).Should().BeGreaterThanOrEqualTo(
            0,
            "Create() must register the trace-context processor, or the exported record carries the drain "
            + "thread's context instead of the captured one");
    }

    [Fact]
    public void Create_ExportsOverHttpProtobuf_WhenNoProtocolIsConfigured()
    {
        var firstBytes = CaptureFirstBytesOfOneExport(protocol: null, logsProtocol: null);

        firstBytes.Should().StartWith(
            "POST /v1/logs HTTP/1.1",
            "the SDK's own default is OTLP/gRPC, which would be sent to the documented HTTP /v1/logs endpoint "
            + "and silently lost, so Create must set the protocol explicitly");
    }

    // THE PROTOCOL IS PINNED, NOT CONFIGURABLE — a cross-SDK parity requirement, not an implementation
    // shortcut. Java (OtlpHttpLogRecordExporter), Python (OTLPLogExporter from
    // opentelemetry.exporter.otlp.proto.http._log_exporter) and JS (@opentelemetry/exporter-logs-otlp-http)
    // each fix the snapshot transport by choosing the HTTP exporter type and read no protocol variable at all.
    // Honoring one here would make .NET the only SDK where a generic distro-wide setting can redirect DI
    // snapshots onto gRPC.
    //
    // Both variables are covered, and BOTH are set to grpc at once: the signal-specific one is what an
    // operator following the OTLP spec would reach for first, so a future "let's respect the spec" change is
    // most likely to reintroduce reading exactly that one.
    [Theory]
    [InlineData("grpc", null)]
    [InlineData(null, "grpc")]
    [InlineData("grpc", "grpc")]
    public void Create_IgnoresTheProtocolEnvironmentVariables(string? protocol, string? logsProtocol)
    {
        var firstBytes = CaptureFirstBytesOfOneExport(protocol, logsProtocol);

        // gRPC always opens with the HTTP/2 connection preface ("PRI * HTTP/2.0"); OTLP/HTTP never does.
        firstBytes.Should().StartWith(
            "POST /v1/logs HTTP/1.1",
            "DI snapshot transport must stay OTLP/HTTP regardless of the protocol variables, matching "
            + "Java/Python/JS, which expose no protocol knob for snapshots");
    }

    // Emits one snapshot at a raw TCP listener and returns the first bytes it receives, which identifies the
    // wire protocol unambiguously.
    private static string CaptureFirstBytesOfOneExport(string? protocol, string? logsProtocol)
    {
        var previousProtocol = Environment.GetEnvironmentVariable(ProtocolVar);
        var previousLogsProtocol = Environment.GetEnvironmentVariable(LogsProtocolVar);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = string.Empty;
        var done = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            try
            {
                using var connection = listener.AcceptTcpClient();
                var buffer = new byte[64];
                var read = connection.GetStream().Read(buffer, 0, buffer.Length);
                received = Encoding.ASCII.GetString(buffer, 0, read);
                RespondOk(connection);
            }
            catch (SocketException)
            {
                // Listener stopped before anything connected; `received` stays empty and the assertion fails
                // with its own message rather than an exception from a background thread.
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true };
        reader.Start();

        try
        {
            // Set BEFORE Create: the exporter resolves its options while the LoggerFactory is built, so a
            // variable set afterwards could not have been read either way and would prove nothing.
            Environment.SetEnvironmentVariable(ProtocolVar, protocol);
            Environment.SetEnvironmentVariable(LogsProtocolVar, logsProtocol);

            var emitter = DISnapshotOtlpEmitter.Create($"http://127.0.0.1:{port}/v1/logs", null);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
            });

            emitter.Dispose();

            done.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the exporter must attempt an export");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProtocolVar, previousProtocol);
            Environment.SetEnvironmentVariable(LogsProtocolVar, previousLogsProtocol);
            listener.Stop();
        }

        return received;
    }

    // Answer the POST instead of dropping the connection: these tests only care about the bytes that went out,
    // and an unanswered request makes the exporter burn its retries and its HTTP timeout on every one of them.
    private static void RespondOk(TcpClient connection)
    {
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        connection.GetStream().Write(response, 0, response.Length);
        connection.GetStream().Flush();
    }

    private static int IndexOfSequence(List<byte> haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}

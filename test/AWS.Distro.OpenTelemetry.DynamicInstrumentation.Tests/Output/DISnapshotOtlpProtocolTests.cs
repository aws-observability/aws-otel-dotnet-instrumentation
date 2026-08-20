// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

/// <summary>
/// Protocol selection for the snapshot exporter, asserted on the BYTES THAT REACH THE SOCKET rather than on
/// the exporter's options. The bug this covers was invisible at the options level: nothing in the DI code set
/// Protocol at all, so the SDK's own default (gRPC) was used against the HTTP <c>/v1/logs</c> endpoint the
/// docs tell operators to configure, and every snapshot was silently lost.
/// </summary>
// Serialized with the other suites that touch process-global state: these tests mutate the OTLP protocol
// environment variables, and DynamicInstrumentationManagerTests builds a real snapshot exporter that READS
// them. xunit runs separate collections in parallel, so without this the two could interleave.
[Collection("SerialProcessState")]
public class DISnapshotOtlpProtocolTests
{
    private const string LogsProtocolVar = "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    [Fact]
    public void DefaultsToHttpProtobuf_WhenNoProtocolIsConfigured()
    {
        var firstBytes = CaptureFirstBytesOfOneExport(protocol: null, logsProtocol: null);

        firstBytes.Should().StartWith(
            "POST /v1/logs HTTP/1.1",
            "with no protocol configured, snapshots must go out as OTLP/HTTP to the documented /v1/logs "
            + "endpoint — the SDK's own default is gRPC, which would be sent to that same HTTP path and lost");
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
    public void IgnoresTheProtocolEnvironmentVariables(string? protocol, string? logsProtocol)
    {
        var firstBytes = CaptureFirstBytesOfOneExport(protocol, logsProtocol);

        // gRPC always opens with the HTTP/2 connection preface ("PRI * HTTP/2.0"); OTLP/HTTP never does.
        firstBytes.Should().StartWith(
            "POST /v1/logs HTTP/1.1",
            "DI snapshot transport must stay OTLP/HTTP regardless of the protocol variables, matching "
            + "Java/Python/JS, which expose no protocol knob for snapshots");
    }

    [Fact]
    public void Create_RegistersTheTraceContextProcessor_SoTheProductionPathStampsNativeIds()
    {
        // The processor's own tests build their LoggerFactory by hand, so deleting the AddProcessor line from
        // Create() left the whole suite green while reintroducing the wrong-trace-context bug. This covers the
        // wiring itself: the captured trace id has to appear in the bytes Create()'s pipeline puts on the wire.
        //
        // Asserted on the OTLP/HTTP body because that is what the exporter Create() builds actually sends; the
        // ids are hex in the protobuf payload, so a substring match over the raw bytes is enough to prove the
        // native fields were stamped rather than left to the ambient (here: absent) Activity.
        const string capturedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string capturedSpanId = "00f067aa0ba902b7";

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
                var buffer = new byte[4096];
                var read = connection.GetStream().Read(buffer, 0, buffer.Length);
                received.AddRange(buffer.Take(read));
            }
            catch (SocketException)
            {
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
            // No protocol variable is set: Create pins OTLP/HTTP, which is what puts the payload in one
            // readable request rather than an HTTP/2 stream.
            var emitter = DISnapshotOtlpEmitter.Create($"http://127.0.0.1:{port}/v1/logs", null);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
                TraceId = capturedTraceId,
                SpanId = capturedSpanId,
                TimestampMs = 1_785_000_000_000,
            });
            emitter.Dispose();

            done.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the exporter must attempt an export");
        }
        finally
        {
            listener.Stop();
        }

        // Trace ids travel as 16 raw bytes in the OTLP payload, so compare bytes rather than hex text.
        var traceIdBytes = Convert.FromHexString(capturedTraceId);
        IndexOfSequence(received, traceIdBytes).Should().BeGreaterThanOrEqualTo(
            0,
            "Create() must register the trace-context processor, or the exported record carries the drain "
            + "thread's context instead of the captured one");
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

    // Emits one snapshot at a raw TCP listener and returns the first bytes it receives, which identifies the
    // wire protocol unambiguously. A raw listener (rather than an HttpListener or a collector) is the point:
    // it accepts the connection whatever the protocol, so a wrong protocol shows up as the wrong bytes
    // instead of as a connection error that both cases would produce.
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
            }
            catch (SocketException)
            {
                // Listener stopped before anything connected; `received` stays empty and the assertion fails
                // with the useful message rather than an exception from a background thread.
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
            // Set before Create: the exporter resolves its protocol while the LoggerFactory is built.
            Environment.SetEnvironmentVariable(ProtocolVar, protocol);
            Environment.SetEnvironmentVariable(LogsProtocolVar, logsProtocol);

            var emitter = DISnapshotOtlpEmitter.Create($"http://127.0.0.1:{port}/v1/logs", null);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
            });

            // Disposing flushes the batch through the exporter, which is what actually opens the connection.
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
}

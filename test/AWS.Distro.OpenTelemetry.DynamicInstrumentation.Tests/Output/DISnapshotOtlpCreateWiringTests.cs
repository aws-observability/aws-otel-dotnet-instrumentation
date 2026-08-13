// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
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
// observable end to end.
[Collection("SerialProcessState")]
public class DISnapshotOtlpCreateWiringTests
{
    private const string CapturedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string CapturedSpanId = "00f067aa0ba902b7";

    [Fact]
    public void Create_RegistersTheTraceContextProcessor_SoExportsCarryTheCapturedTraceId()
    {
        var protocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
        var previousProtocol = Environment.GetEnvironmentVariable(protocolVar);

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
            // http/protobuf so the payload lands in one readable request rather than an HTTP/2 stream.
            Environment.SetEnvironmentVariable(protocolVar, "http/protobuf");

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
            Environment.SetEnvironmentVariable(protocolVar, previousProtocol);
            listener.Stop();
        }

        // A trace id travels as 16 raw bytes in the OTLP payload, so match bytes rather than hex text.
        IndexOfSequence(received, Convert.FromHexString(CapturedTraceId)).Should().BeGreaterThanOrEqualTo(
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
}

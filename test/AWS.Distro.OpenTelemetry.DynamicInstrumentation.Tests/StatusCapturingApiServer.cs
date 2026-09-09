// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests;

/// <summary>
/// A minimal stand-in for the local CloudWatch Agent's Dynamic Instrumentation API, recording the status
/// reports the agent under test sends. Lets a test drive the real manager — real HttpClient, real client,
/// real reporter — and assert on what actually went over the wire.
/// </summary>
internal sealed class StatusCapturingApiServer : IDisposable
{
    private readonly HttpListener listener;
    private readonly List<string> statusBodies;
    private readonly ManualResetEventSlim statusReceived = new(false);
    private readonly Thread thread;

    private StatusCapturingApiServer(HttpListener listener, string url, List<string> statusBodies)
    {
        this.listener = listener;
        this.Url = url;
        this.statusBodies = statusBodies;
        this.thread = new Thread(this.Serve) { IsBackground = true, Name = "TestDiApi" };
        this.thread.Start();
    }

    public string Url { get; }

    public static StatusCapturingApiServer Start(List<string> statusBodies)
    {
        // Bind a throwaway socket to port 0 to have the OS pick a free port, then hand it to HttpListener.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        return new StatusCapturingApiServer(listener, $"http://127.0.0.1:{port}", statusBodies);
    }

    /// <summary>
    /// Waits (bounded) for a status report whose body contains <paramref name="text"/>.
    /// </summary>
    /// <param name="text">Substring to look for across all received status bodies.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <returns>True if a matching status arrived within the timeout.</returns>
    public bool WaitForStatusContaining(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (this.statusBodies)
            {
                if (this.statusBodies.Any(b => b.Contains(text, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            this.statusReceived.Wait(TimeSpan.FromMilliseconds(50));
            this.statusReceived.Reset();
        }

        return false;
    }

    public void Dispose()
    {
        this.listener.Close(); // Aborts the blocking GetContext, ending the serve loop.
        this.thread.Join(TimeSpan.FromSeconds(2));
        this.statusReceived.Dispose();
    }

    private void Serve()
    {
        while (this.listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = this.listener.GetContext();
            }
            catch (Exception)
            {
                // Listener closed (test finished) or a client aborted; either way there is nothing to serve.
                return;
            }

            try
            {
                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                string response;

                if (path.Contains("report-instrumentation-configuration-status", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    lock (this.statusBodies)
                    {
                        this.statusBodies.Add(body);
                    }

                    this.statusReceived.Set();
                    response = "{}";
                }
                else
                {
                    // Configuration fetches: answer "nothing new" so the poller stays quiet and the test
                    // controls the configuration set by calling OnConfigurationsChanged directly.
                    response = """{ "Changed": false }""";
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // A failed response must not kill the server thread; the test's assertions cover the outcome.
            }
        }
    }
}

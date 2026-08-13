// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var endpoint = (IPEndPoint)listener.LocalEndpoint;

var server = Task.Run(() =>
{
    using var client = listener.AcceptTcpClient();
    using var stream = client.GetStream();
    var request = new byte[4096];
    _ = stream.Read(request, 0, request.Length);
    var response = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
    stream.Write(response, 0, response.Length);
});

using var httpClient = new HttpClient();
using var response = await httpClient.GetAsync($"http://127.0.0.1:{endpoint.Port}/smoke-request");
response.EnsureSuccessStatusCode();
await server;

// Allow the periodic console metric reader to export before process shutdown.
await Task.Delay(TimeSpan.FromSeconds(2));
Console.WriteLine("CloudWatch plugin smoke test completed.");

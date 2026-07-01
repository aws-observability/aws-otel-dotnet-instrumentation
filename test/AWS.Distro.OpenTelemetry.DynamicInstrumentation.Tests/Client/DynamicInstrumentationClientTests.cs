// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Client;

public class DynamicInstrumentationClientTests
{
    private static DynamicInstrumentationClient CreateClient(MockHttpHandler handler) =>
        new(new HttpClient(handler), "http://localhost:2000", "test-service", "test-env");

    [Fact]
    public async Task FetchConfigurations_ReturnsConfigs_OnSuccess()
    {
        var responseJson = """
        {
            "Changed": true,
            "SyncedAt": 1000,
            "SyncInterval": 60,
            "LatestConfigurations": [
                {
                    "InstrumentationType": "PROBE",
                    "LocationHash": "aabb000000000001",
                    "Location": { "CodeLocation": { "Language": "Dotnet", "ClassName": "OrderService", "MethodName": "Process" } }
                }
            ]
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Changed.Should().BeTrue();
        result.SyncedAt.Should().Be(1000);
        result.SyncInterval.Should().Be(60);
        result.Configurations.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsUnchanged_WhenChangedIsFalse()
    {
        var responseJson = """{ "Changed": false, "SyncedAt": 500 }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.BREAKPOINT, syncedAt: 400);

        result.Changed.Should().BeFalse();
        result.Configurations.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchConfigurations_HandlesPagination()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "Changed": true,
                        "SyncedAt": 1000,
                        "NextToken": "page2",
                        "LatestConfigurations": [{ "LocationHash": "config1" }]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "Changed": true,
                    "SyncedAt": 1000,
                    "LatestConfigurations": [{ "LocationHash": "config2" }]
                }
                """, Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        callCount.Should().Be(2);
        result.Configurations.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchConfigurations_StopsAtMaxPages()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(_ =>
        {
            callCount++;
            var json = "{\"Changed\":true,\"SyncedAt\":1000,\"NextToken\":\"page" + (callCount + 1) + "\",\"LatestConfigurations\":[{\"LocationHash\":\"config" + callCount + "\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        callCount.Should().Be(3); // MaxPages = 3
        result.Configurations.Should().HaveCount(3);
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsEmpty_OnHttpError()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "");
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Changed.Should().BeFalse();
        result.Configurations.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsEmpty_OnException()
    {
        var handler = new MockHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Changed.Should().BeFalse();
        result.Configurations.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchConfigurations_SendsCorrectRequestBody()
    {
        string? capturedBody = null;
        var handler = new MockHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "Changed": false, "SyncedAt": 0 }""", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        await client.FetchConfigurationsAsync(InstrumentationType.BREAKPOINT, syncedAt: 12345);

        capturedBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("service").GetString().Should().Be("test-service");
        doc.RootElement.GetProperty("environment").GetString().Should().Be("test-env");
        doc.RootElement.GetProperty("instrumentationType").GetString().Should().Be("BREAKPOINT");
        doc.RootElement.GetProperty("syncedAt").GetInt64().Should().Be(12345);
    }

    [Fact]
    public async Task ReportStatus_SendsRequest()
    {
        string? capturedBody = null;
        var handler = new MockHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = CreateClient(handler);
        await client.ReportStatusAsync(new List<StatusEntry>
        {
            new() { LocationHash = "hash1", Status = "READY", InstrumentationType = "PROBE" }
        });

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("hash1");
        capturedBody.Should().Contain("READY");
    }

    [Fact]
    public async Task ReportStatus_SkipsEmptyList()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(_ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK); });

        var client = CreateClient(handler);
        await client.ReportStatusAsync(new List<StatusEntry>());

        callCount.Should().Be(0);
    }
}

// --- Test Helper ---

internal class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public MockHttpHandler(HttpStatusCode status, string content)
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        })
    { }

    public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_handler(request));
}

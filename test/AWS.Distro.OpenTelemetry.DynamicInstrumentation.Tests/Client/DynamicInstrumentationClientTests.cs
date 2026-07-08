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
            "SyncedAt": "2024-09-17T22:03:24Z",
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

        result.Success.Should().BeTrue();
        result.Changed.Should().BeTrue();
        result.SyncedAt.Should().Be("2024-09-17T22:03:24Z");
        result.SyncInterval.Should().Be(60);
        result.Configurations.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchConfigurations_ParsesIso8601SyncedAt_WithoutFailing()
    {
        // Regression: the spec types SyncedAt as an ISO-8601 string. Modeling it as long?
        // made System.Text.Json throw on every real response, so every poll returned
        // Failed and the client never synced. This proves the string wire value parses.
        var responseJson = """{ "Changed": false, "SyncedAt": "2024-09-17T22:08:24Z" }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(
            InstrumentationType.PROBE, syncedAt: "2024-09-17T22:03:24Z");

        result.Success.Should().BeTrue();
        result.Changed.Should().BeFalse();
        result.SyncedAt.Should().Be("2024-09-17T22:08:24Z");
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsUnchanged_WhenChangedIsFalse()
    {
        var responseJson = """{ "Changed": false, "SyncedAt": "2024-09-17T22:08:24Z" }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(
            InstrumentationType.BREAKPOINT, syncedAt: "2024-09-17T22:03:24Z");

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
                        "SyncedAt": "2024-09-17T22:03:24Z",
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
                    "SyncedAt": "2024-09-17T22:03:24Z",
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
    public async Task FetchConfigurations_LatchesChangedFromFirstPage_NoDataLoss()
    {
        // Regression: `changed` was reassigned every page. A continuation page with
        // Changed=false used to overwrite the latched true, break the loop, and return
        // changed=false — the poller then dropped the page-1 configs (data loss). `changed`
        // must latch from page 0 and the accumulated configs must survive.
        int callCount = 0;
        var handler = new MockHttpHandler(_ =>
        {
            callCount++;
            var json = callCount == 1
                ? """{ "Changed": true, "SyncedAt": "2024-09-17T22:03:24Z", "NextToken": "page2", "LatestConfigurations": [{ "LocationHash": "config1" }] }"""
                : """{ "Changed": false, "SyncedAt": "2024-09-17T22:03:24Z", "LatestConfigurations": [{ "LocationHash": "config2" }] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Changed.Should().BeTrue("changed latches from the first page");
        result.Configurations.Should().HaveCount(2, "page-1 configs must not be lost when page 2 says Changed=false");
    }

    [Fact]
    public async Task FetchConfigurations_StopsAtMaxPages()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(_ =>
        {
            callCount++;
            var json = "{\"Changed\":true,\"SyncedAt\":\"2024-09-17T22:03:24Z\",\"NextToken\":\"page" + (callCount + 1) + "\",\"LatestConfigurations\":[{\"LocationHash\":\"config" + callCount + "\"}]}";
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
    public async Task FetchConfigurations_MidPaginationFailure_ReturnsFailure()
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
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var client = CreateClient(handler);
        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Success.Should().BeFalse();
        result.Configurations.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsFailure_OnHttpError()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "");
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Success.Should().BeFalse();
        result.Configurations.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsFailure_OnException()
    {
        var handler = new MockHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Success.Should().BeFalse();
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
                Content = new StringContent("""{ "Changed": false }""", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        await client.FetchConfigurationsAsync(
            InstrumentationType.BREAKPOINT, syncedAt: "2024-09-17T22:03:24Z");

        capturedBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(capturedBody!);
        // Request fields must be PascalCase to match the API (and the Java/Python/Node SDKs).
        // A case-sensitive backend treats camelCase as missing → empty/unfiltered configs.
        doc.RootElement.GetProperty("Service").GetString().Should().Be("test-service");
        doc.RootElement.GetProperty("Environment").GetString().Should().Be("test-env");
        doc.RootElement.GetProperty("InstrumentationType").GetString().Should().Be("BREAKPOINT");
        // SyncedAt must serialize as an ISO-8601 string on the wire (spec contract).
        doc.RootElement.GetProperty("SyncedAt").GetString().Should().Be("2024-09-17T22:03:24Z");
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

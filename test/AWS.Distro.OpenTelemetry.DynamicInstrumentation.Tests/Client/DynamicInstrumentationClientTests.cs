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

    // Parses a JSON literal (e.g. "\"abc\"" or "123") into a standalone JsonElement, mirroring how
    // the client captures the opaque SyncedAt cursor from a response for echo-back.
    private static JsonElement Json(string literal) => JsonDocument.Parse(literal).RootElement.Clone();

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
        result.SyncedAt!.Value.GetString().Should().Be("2024-09-17T22:03:24Z");
        result.SyncInterval.Should().Be(60);
        result.Configurations.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchConfigurations_ParsesIso8601SyncedAt_WithoutFailing()
    {
        // The API spec types SyncedAt as an ISO-8601 string. SyncedAt is a JsonElement so it
        // round-trips either wire shape; here the string form must parse without failing.
        var responseJson = """{ "Changed": false, "SyncedAt": "2024-09-17T22:08:24Z" }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(
            InstrumentationType.PROBE, syncedAt: Json("\"2024-09-17T22:03:24Z\""));

        result.Success.Should().BeTrue();
        result.Changed.Should().BeFalse();
        result.SyncedAt!.Value.GetString().Should().Be("2024-09-17T22:08:24Z");
    }

    [Fact]
    public async Task FetchConfigurations_ParsesNumericSyncedAt_FromLiveBackend()
    {
        // Verified against the live backend (application-signals.us-east-1.api.aws): it emits
        // SyncedAt as a JSON NUMBER in exponent form, NOT the ISO string the spec documents.
        // Modeling SyncedAt as string? made System.Text.Json throw here, so every poll returned
        // Failed and the client silently never synced. This pins the real wire shape.
        var responseJson = """{ "Changed": true, "SyncInterval": 300, "SyncedAt": 1.7839751E9, "LatestConfigurations": [] }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);

        result.Success.Should().BeTrue("the live backend returns a numeric SyncedAt and the client must parse it");
        result.Changed.Should().BeTrue();
        result.SyncedAt!.Value.ValueKind.Should().Be(JsonValueKind.Number);
        result.SyncedAt!.Value.GetRawText().Should().Be("1.7839751E9");
    }

    [Fact]
    public async Task FetchConfigurations_ReturnsUnchanged_WhenChangedIsFalse()
    {
        var responseJson = """{ "Changed": false, "SyncedAt": "2024-09-17T22:08:24Z" }""";

        var handler = new MockHttpHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var result = await client.FetchConfigurationsAsync(
            InstrumentationType.BREAKPOINT, syncedAt: Json("\"2024-09-17T22:03:24Z\""));

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
        HttpContent? capturedContent = null;
        var handler = new MockHttpHandler(req =>
        {
            capturedContent = req.Content;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "Changed": false }""", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler);
        await client.FetchConfigurationsAsync(
            InstrumentationType.BREAKPOINT, syncedAt: Json("\"2024-09-17T22:03:24Z\""));

        capturedContent.Should().NotBeNull();
        var capturedBody = await capturedContent!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(capturedBody);
        // Request fields must be PascalCase to match the API (and the Java/Python/Node SDKs).
        // A case-sensitive backend treats camelCase as missing → empty/unfiltered configs.
        doc.RootElement.GetProperty("Service").GetString().Should().Be("test-service");
        doc.RootElement.GetProperty("Environment").GetString().Should().Be("test-env");
        doc.RootElement.GetProperty("InstrumentationType").GetString().Should().Be("BREAKPOINT");
        // SyncedAt echoes back verbatim in the shape it was received (here a string).
        doc.RootElement.GetProperty("SyncedAt").GetString().Should().Be("2024-09-17T22:03:24Z");
    }

    [Fact]
    public async Task FetchConfigurations_EchoesNumericSyncedAt_BackAsNumber()
    {
        // The round-trip that broke against the live backend: a numeric SyncedAt received from
        // the server must be echoed back on the next request AS A NUMBER, not a quoted string,
        // or the backend rejects the cursor. Guards the JsonElement round-trip end to end.
        HttpContent? capturedContent = null;
        var handler = new MockHttpHandler(req =>
        {
            capturedContent = req.Content;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "Changed": false }""", Encoding.UTF8, "application/json"),
            };
        });

        var client = CreateClient(handler);
        await client.FetchConfigurationsAsync(
            InstrumentationType.PROBE, syncedAt: Json("1.7839751E9"));

        var capturedBody = await capturedContent!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(capturedBody);
        doc.RootElement.GetProperty("SyncedAt").ValueKind.Should().Be(JsonValueKind.Number);
        doc.RootElement.GetProperty("SyncedAt").GetRawText().Should().Be("1.7839751E9");
    }

    [Fact]
    public async Task ReportStatus_SendsRequest()
    {
        HttpContent? capturedContent = null;
        var handler = new MockHttpHandler(req =>
        {
            capturedContent = req.Content;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = CreateClient(handler);
        await client.ReportStatusAsync(new List<StatusEntry>
        {
            new() { LocationHash = "hash1", Status = "READY", InstrumentationType = "PROBE" }
        });

        capturedContent.Should().NotBeNull();
        var capturedBody = await capturedContent!.ReadAsStringAsync();
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

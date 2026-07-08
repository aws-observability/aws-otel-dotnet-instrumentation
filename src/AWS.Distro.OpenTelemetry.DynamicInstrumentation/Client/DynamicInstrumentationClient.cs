// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;

public class DynamicInstrumentationClient
{
    private const int MaxPages = 3;

    private readonly ILogger _log;
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _serviceName;
    private readonly string _environment;

    // PascalCase on the wire (no camelCase policy) to match the API and the other SDKs.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DynamicInstrumentationClient(HttpClient httpClient, string apiUrl, string serviceName, string environment, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiUrl = apiUrl;
        _serviceName = serviceName;
        _environment = environment;
        _log = logger ?? NullLogger.Instance;
    }

    public async Task<FetchConfigurationsResponse> FetchConfigurationsAsync(
        InstrumentationType type, string? syncedAt = null, CancellationToken ct = default)
    {
        var allConfigs = new List<JsonElement>();
        string? nextToken = null;
        string? responseSyncedAt = null;
        bool changed = false;
        int? syncInterval = null;

        for (int page = 0; page < MaxPages; page++)
        {
            var request = new FetchConfigurationsRequest
            {
                Service = _serviceName,
                Environment = _environment,
                InstrumentationType = type.ToString(),
                SyncedAt = syncedAt,
                NextToken = nextToken
            };

            var response = await PostAsync<FetchConfigurationsRequest, FetchConfigurationsRawResponse>(
                $"{_apiUrl}/list-instrumentation-configurations", request, ct);

            if (response == null)
                return FetchConfigurationsResponse.Failed;

            // Latch `changed` from page 0 only — a later page's Changed=false must not
            // drop configs already accumulated (data loss).
            if (page == 0)
                changed = response.Changed;
            responseSyncedAt = response.SyncedAt;
            syncInterval = response.SyncInterval;

            if (!changed)
                break;

            if (response.LatestConfigurations != null)
                allConfigs.AddRange(response.LatestConfigurations);

            nextToken = response.NextToken;
            if (string.IsNullOrEmpty(nextToken))
                break;
        }

        return new FetchConfigurationsResponse(true, changed, responseSyncedAt, syncInterval, allConfigs.ToArray());
    }

    public async Task ReportStatusAsync(List<StatusEntry> statuses, CancellationToken ct = default)
    {
        if (statuses == null || statuses.Count == 0)
            return;

        var request = new ReportStatusRequest
        {
            Service = _serviceName,
            Environment = _environment,
            Configurations = statuses
        };

        await PostAsync<ReportStatusRequest, object>(
            $"{_apiUrl}/report-instrumentation-configuration-status", request, ct);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken ct)
    {
        try
        {
            using var suppressScope = SuppressInstrumentationScope.Begin();

            var response = await _httpClient.PostAsJsonAsync(url, body, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
                return default;

            var content = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(content))
                return default;

            return JsonSerializer.Deserialize<TResponse>(content, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DI client request to {Url} failed", url);
            return default;
        }
    }
}

// --- Request/Response Models ---

public record FetchConfigurationsRequest
{
    public string Service { get; init; } = "";
    public string Environment { get; init; } = "";
    public string InstrumentationType { get; init; } = "";
    public string? SyncedAt { get; init; }
    public string? NextToken { get; init; }
}

internal record FetchConfigurationsRawResponse
{
    [JsonPropertyName("Changed")]
    public bool Changed { get; init; }

    // Opaque cursor token (ISO-8601 string per spec) — echoed back, never parsed.
    [JsonPropertyName("SyncedAt")]
    public string? SyncedAt { get; init; }

    [JsonPropertyName("SyncInterval")]
    public int? SyncInterval { get; init; }

    [JsonPropertyName("NextToken")]
    public string? NextToken { get; init; }

    [JsonPropertyName("LatestConfigurations")]
    public JsonElement[]? LatestConfigurations { get; init; }
}

public record FetchConfigurationsResponse(
    bool Success,
    bool Changed,
    string? SyncedAt,
    // TODO: wire SyncInterval into ConfigurationPoller for dynamic backpressure
    int? SyncInterval,
    JsonElement[] Configurations)
{
    public static readonly FetchConfigurationsResponse Failed = new(false, false, null, null, Array.Empty<JsonElement>());
}

public record StatusEntry
{
    public string InstrumentationType { get; init; } = "";
    public string LocationHash { get; init; } = "";
    public string Status { get; init; } = "";
    public string? DisableReason { get; init; }
    public string? ErrorCause { get; init; }
    public string? Timestamp { get; init; }
}

internal record ReportStatusRequest
{
    public string Service { get; init; } = "";
    public string Environment { get; init; } = "";
    public List<StatusEntry> Configurations { get; init; } = new();
}


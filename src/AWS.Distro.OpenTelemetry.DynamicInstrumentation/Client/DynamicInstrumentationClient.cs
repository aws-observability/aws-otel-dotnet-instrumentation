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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
        InstrumentationType type, long? syncedAt = null, CancellationToken ct = default)
    {
        var allConfigs = new List<JsonElement>();
        string? nextToken = null;
        long? responseSyncedAt = null;
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

        return new FetchConfigurationsResponse(true, changed, responseSyncedAt ?? 0, syncInterval, allConfigs.ToArray());
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
    public long? SyncedAt { get; init; }
    public string? NextToken { get; init; }
}

internal record FetchConfigurationsRawResponse
{
    [JsonPropertyName("Changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("SyncedAt")]
    public long? SyncedAt { get; init; }

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
    long SyncedAt,
    // TODO: wire SyncInterval into ConfigurationPoller for dynamic backpressure
    int? SyncInterval,
    JsonElement[] Configurations)
{
    public static readonly FetchConfigurationsResponse Failed = new(false, false, 0, null, Array.Empty<JsonElement>());
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


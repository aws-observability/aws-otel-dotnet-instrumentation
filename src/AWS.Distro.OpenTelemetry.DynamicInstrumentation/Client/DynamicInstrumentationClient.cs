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

/// <summary>
/// HTTP client for the Dynamic Instrumentation configuration and status-reporting API.
/// </summary>
/// <param name="httpClient">The HTTP client used for requests.</param>
/// <param name="apiUrl">Base URL of the configuration API.</param>
/// <param name="serviceName">Service name sent with each request.</param>
/// <param name="environment">Deployment environment sent with each request.</param>
/// <param name="logger">Optional logger; defaults to a no-op logger.</param>
public class DynamicInstrumentationClient(HttpClient httpClient, string apiUrl, string serviceName, string environment, ILogger? logger = null)
{
    private const int MaxPages = 3;

    // PascalCase on the wire (no camelCase policy) to match the API and the other SDKs.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger log = logger ?? NullLogger.Instance;
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string apiUrl = apiUrl;
    private readonly string serviceName = serviceName;
    private readonly string environment = environment;

    /// <summary>Fetches instrumentation configurations of the given type, following pagination.</summary>
    /// <param name="type">The instrumentation type to fetch.</param>
    /// <param name="syncedAt">Opaque cursor from the previous response, or null for a full sync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fetch result, including whether anything changed.</returns>
    public async Task<FetchConfigurationsResponse> FetchConfigurationsAsync(
        InstrumentationType type, string? syncedAt = null, CancellationToken ct = default)
    {
        var allConfigs = new List<JsonElement>();
        string? nextToken = null;
        string? responseSyncedAt = null;
        var changed = false;
        int? syncInterval = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var request = new FetchConfigurationsRequest
            {
                Service = this.serviceName,
                Environment = this.environment,
                InstrumentationType = type.ToString(),
                SyncedAt = syncedAt,
                NextToken = nextToken,
            };

            var response = await this.PostAsync<FetchConfigurationsRequest, FetchConfigurationsRawResponse>(
                $"{this.apiUrl}/list-instrumentation-configurations", request, ct);

            if (response == null)
            {
                return FetchConfigurationsResponse.Failed;
            }

            // Latch `changed` from page 0 only — a later page's Changed=false must not
            // drop configs already accumulated (data loss).
            if (page == 0)
            {
                changed = response.Changed;
            }

            responseSyncedAt = response.SyncedAt;
            syncInterval = response.SyncInterval;

            if (!changed)
            {
                break;
            }

            if (response.LatestConfigurations != null)
            {
                allConfigs.AddRange(response.LatestConfigurations);
            }

            nextToken = response.NextToken;
            if (string.IsNullOrEmpty(nextToken))
            {
                break;
            }
        }

        return new FetchConfigurationsResponse(true, changed, responseSyncedAt, syncInterval, [.. allConfigs]);
    }

    /// <summary>Reports instrumentation status events back to the API.</summary>
    /// <param name="statuses">The status entries to report; no-op if empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the report has been sent.</returns>
    public async Task ReportStatusAsync(List<StatusEntry> statuses, CancellationToken ct = default)
    {
        if (statuses == null || statuses.Count == 0)
        {
            return;
        }

        var request = new ReportStatusRequest
        {
            Service = this.serviceName,
            Environment = this.environment,
            Configurations = statuses,
        };

        await this.PostAsync<ReportStatusRequest, object>(
            $"{this.apiUrl}/report-instrumentation-configuration-status", request, ct);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken ct)
    {
        try
        {
            using var suppressScope = SuppressInstrumentationScope.Begin();

            var response = await this.httpClient.PostAsJsonAsync(url, body, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(content))
            {
                return default;
            }

            return JsonSerializer.Deserialize<TResponse>(content, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.log.LogDebug(ex, "DI client request to {Url} failed", url);
            return default;
        }
    }
}

// --- Request/Response Models ---

/// <summary>Request body for listing instrumentation configurations.</summary>
public record FetchConfigurationsRequest
{
    /// <summary>Gets the service name.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>Gets the deployment environment.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>Gets the instrumentation type being requested.</summary>
    public string InstrumentationType { get; init; } = string.Empty;

    /// <summary>Gets the opaque sync cursor from the previous response, if any.</summary>
    public string? SyncedAt { get; init; }

    /// <summary>Gets the pagination token, if continuing a prior page.</summary>
    public string? NextToken { get; init; }
}

/// <summary>Aggregated result of a (possibly paginated) list request.</summary>
/// <param name="Success">Whether the request succeeded.</param>
/// <param name="Changed">Whether configurations changed since the last sync.</param>
/// <param name="SyncedAt">Opaque cursor to echo on the next request.</param>
/// <param name="SyncInterval">Server-suggested seconds before the next sync.</param>
/// <param name="Configurations">The accumulated configuration elements.</param>
public record FetchConfigurationsResponse(
    bool Success,
    bool Changed,
    string? SyncedAt,
    int? SyncInterval,
    JsonElement[] Configurations)
{
    // TODO: wire SyncInterval into ConfigurationPoller for dynamic backpressure.

    /// <summary>A shared failure result with no configurations.</summary>
    public static readonly FetchConfigurationsResponse Failed = new(false, false, null, null, []);
}

/// <summary>A status event reported for a single instrumentation configuration.</summary>
public record StatusEntry
{
    /// <summary>Gets the instrumentation type.</summary>
    public string InstrumentationType { get; init; } = string.Empty;

    /// <summary>Gets the location hash identifying the configuration.</summary>
    public string LocationHash { get; init; } = string.Empty;

    /// <summary>Gets the status value (e.g. READY, ACTIVE, ERROR, DISABLED).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the disable reason, if the status is DISABLED.</summary>
    public string? DisableReason { get; init; }

    /// <summary>Gets the error cause, if the status is ERROR.</summary>
    public string? ErrorCause { get; init; }

    /// <summary>Gets the timestamp of the status event.</summary>
    public string? Timestamp { get; init; }
}

/// <summary>Raw deserialized response for a single page of the list API.</summary>
internal record FetchConfigurationsRawResponse
{
    /// <summary>Gets a value indicating whether configurations changed since the last sync.</summary>
    [JsonPropertyName("Changed")]
    public bool Changed { get; init; }

    /// <summary>Gets the opaque cursor token (ISO-8601 string per spec) — echoed back, never parsed.</summary>
    [JsonPropertyName("SyncedAt")]
    public string? SyncedAt { get; init; }

    /// <summary>Gets the server-suggested seconds to wait before the next sync.</summary>
    [JsonPropertyName("SyncInterval")]
    public int? SyncInterval { get; init; }

    /// <summary>Gets the pagination token for the next page, if any.</summary>
    [JsonPropertyName("NextToken")]
    public string? NextToken { get; init; }

    /// <summary>Gets the configurations returned on this page.</summary>
    [JsonPropertyName("LatestConfigurations")]
    public JsonElement[]? LatestConfigurations { get; init; }
}

/// <summary>Request body for reporting instrumentation status events.</summary>
internal record ReportStatusRequest
{
    /// <summary>Gets the service name.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>Gets the deployment environment.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>Gets the status entries being reported.</summary>
    public List<StatusEntry> Configurations { get; init; } = [];
}

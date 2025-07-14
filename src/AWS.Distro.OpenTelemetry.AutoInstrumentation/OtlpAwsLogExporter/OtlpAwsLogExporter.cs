// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0
// Modifications Copyright The OpenTelemetry Authors. Licensed under the Apache License 2.0 License.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

#pragma warning disable CS1700 // Assembly reference is invalid and cannot be resolved
[assembly: InternalsVisibleTo("AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests, PublicKey=6ba7de5ce46d6af3")]

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// This exporter OVERRIDES the Export functionality of the http/protobuf OtlpLogExporter to allow logs to be exported
/// to the CloudWatch OTLP endpoint https://logs.[AWSRegion].amazonaws.com/v1/logs. Utilizes the AWSSDK
/// library to sign and directly inject SigV4 Authentication to the exported request's headers.
///
/// NOTE: In order to properly configure the usage of this exporter. Please make sure you have the
/// following environment variables:
///
///     export OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=https://logs.[AWSRegion].amazonaws.com/v1/logs
///     export OTEL_AWS_SIG_V4_ENABLED=true
///     export OTEL_EXPORTER_OTLP_LOGS_HEADERS=x-aws-log-group=your-log-group,x-aws-log-stream=your-log-stream
///
/// </summary>
/// <remarks>
/// For more information, see AWS documentation on CloudWatch OTLP Endpoint.
/// </remarks>
public class OtlpAwsLogExporter : BaseExporter<LogRecord>
#pragma warning restore CS1700 // Assembly reference is invalid and cannot be resolved
{
    private static readonly string ServiceName = "logs";
    private static readonly string ContentType = "application/x-protobuf";
#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpAwsLogExporter>();
    private static readonly string OtelExporterOtlpLogsHeadersConfig = "OTEL_EXPORTER_OTLP_LOGS_HEADERS";
    private readonly HttpClient client = new HttpClient();
    private readonly Uri endpoint;
    private readonly string region;
    private readonly int timeout;
    private readonly Resource processResource;
    private readonly Dictionary<string, string> headers;
    private IAwsAuthenticator authenticator;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsLogExporter"/> class.
    /// </summary>
    /// <param name="options">OpenTelemetry Protocol (OTLP) exporter options.</param>
    /// <param name="processResource">Otel Resource Object</param>
    public OtlpAwsLogExporter(OtlpExporterOptions options, Resource processResource)
        : this(options, processResource, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsLogExporter"/> class.
    /// </summary>
    /// <param name="options">OpenTelemetry Protocol (OTLP) exporter options.</param>
    /// <param name="processResource">Otel Resource Object</param>
    /// <param name="authenticator">The authentication used to sign the request with SigV4</param>
    internal OtlpAwsLogExporter(OtlpExporterOptions options, Resource processResource, IAwsAuthenticator? authenticator = null)
    {
        this.endpoint = options.Endpoint;
        this.timeout = options.TimeoutMilliseconds;

        // Verified in Plugin.cs that the endpoint matches the CloudWatch endpoint format.
        this.region = this.endpoint.AbsoluteUri.Split('.')[1];
        this.processResource = processResource;
        this.authenticator = authenticator == null ? new DefaultAwsAuthenticator() : authenticator;
        this.headers = ParseHeaders(System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_HEADERS"));
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        using IDisposable scope = SuppressInstrumentationScope.Begin();

        // Inheriting the size from upstream: https://github.com/open-telemetry/opentelemetry-dotnet/blob/24a13ab91c9c152d03fd0871bbb94e8f6ef08698/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/OtlpLogExporter.cs#L28-L31
        byte[] serializedData = new byte[750000];
        int serializedDataLength = OtlpExporterUtils.WriteLogsData(ref serializedData, 0, this.processResource, batch);

        if (serializedDataLength == -1)
        {
            Logger.LogError("Logs cannot be serialized");
            return ExportResult.Failure;
        }

        try
        {
            HttpResponseMessage? message = Task.Run(() =>
             {
                 // The retry delay cannot exceed the configured timeout period for otlp exporter.
                 // If the backend responds with `RetryAfter` duration that would result in exceeding the configured timeout period
                 // we would fail and drop the data.
                 return RetryHelper.ExecuteWithRetryAsync(() => this.InjectSigV4AndSendAsync(serializedData, 0, serializedDataLength), TimeSpan.FromMilliseconds(this.timeout));
             }).GetAwaiter().GetResult();

            if (message == null || message.StatusCode != HttpStatusCode.OK)
            {
                return ExportResult.Failure;
            }
        }
        catch (Exception)
        {
            return ExportResult.Failure;
        }

        return ExportResult.Success;
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        return base.OnShutdown(timeoutMilliseconds);
    }

    // Creates the UserAgent for the headers. See:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/OtlpExporterOptions.cs#L223
    private static string GetUserAgentString()
    {
        var assembly = typeof(OtlpExporterOptions).Assembly;
        return $"OTel-OTLP-Exporter-Dotnet/{GetPackageVersion(assembly)}";
    }

    // Creates the DotNet instrumentation version for UserAgent header. See:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/Shared/AssemblyVersionExtensions.cs#L49
    private static string GetPackageVersion(Assembly assembly)
    {
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Debug.Assert(!string.IsNullOrEmpty(informationalVersion), "AssemblyInformationalVersionAttribute was not found in assembly");

        var indexOfPlusSign = informationalVersion!.IndexOf('+');
        return indexOfPlusSign > 0
            ? informationalVersion.Substring(0, indexOfPlusSign)
            : informationalVersion;
    }

    private static Dictionary<string, string> ParseHeaders(string? headersString)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(headersString))
        {
            var headerPairs = headersString.Split(',');
            foreach (var pair in headerPairs)
            {
                var keyValue = pair.Split('=');
                if (keyValue.Length == 2)
                {
                    headers[keyValue[0].Trim()] = keyValue[1].Trim();
                }
            }
        }
        return headers;
    }

    private async Task<HttpResponseMessage> InjectSigV4AndSendAsync(byte[] serializedLogs, int offset, int serializedDataLength)
    {
        Logger.LogInformation("Attempting to send logs");
        if (!this.headers.TryGetValue("x-aws-log-group", out var logGroup) ||
            !this.headers.TryGetValue("x-aws-log-stream", out var logStream))
        {
            Logger.LogError("Log group and stream must be specified in OTEL_EXPORTER_OTLP_LOGS_HEADERS");
            throw new InvalidOperationException("Missing required log group or stream headers");
        }

        Logger.LogInformation($"Using log group: {logGroup}, stream: {logStream}");

        HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, this.endpoint.AbsoluteUri);
        IRequest sigV4Request = await this.GetSignedSigV4Request(serializedLogs, offset, serializedDataLength);

        sigV4Request.Headers.Remove("content-type");
        sigV4Request.Headers.Add("User-Agent", GetUserAgentString());

        // Add headers from environment variable
        foreach (var header in this.headers)
        {
            sigV4Request.Headers.Add(header.Key, header.Value);
        }

        foreach (var header in sigV4Request.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var content = new ByteArrayContent(serializedLogs, offset, serializedDataLength);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);

        httpRequest.Method = HttpMethod.Post;
        httpRequest.Content = content;

        return await this.client.SendAsync(httpRequest);
    }

    private async Task<IRequest> GetSignedSigV4Request(byte[] content, int offset, int serializedDataLength)
    {
        IRequest request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), ServiceName)
        {
            HttpMethod = "POST",
            ContentStream = new MemoryStream(content, offset, serializedDataLength),
            Endpoint = this.endpoint,
            SignatureVersion = SignatureVersion.SigV4,
        };

        AmazonXRayConfig config = new AmazonXRayConfig()
        {
            AuthenticationRegion = this.region,
            UseHttp = false,
            ServiceURL = this.endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
        };

        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync();

        // Need to explicitly add this for using temporary security credentials from AWS STS.
        // SigV4 signing library does not automatically add this header.
        if (credentials.UseToken && credentials.Token != null)
        {
            request.Headers.Add("x-amz-security-token", credentials.Token);
        }

        request.Headers.Add("Host", this.endpoint.Host);
        request.Headers.Add("content-type", ContentType);

        this.authenticator.Sign(request, config, credentials);

        return request;
    }

    private class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}

// Implementation based on:
// https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L41
internal class RetryHelper
{
    private const int InitialBackoffMilliseconds = 1000;
    private const int MaxBackoffMilliseconds = 5000;
    private const double BackoffMultiplier = 1.5;

    // This is to ensure there is no flakiness with the number of times logs are exported in the retry window. Not part of the upstream's implementation
    private const int BufferWindow = 20;
#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpAwsLogExporter>();

#if !NET6_0_OR_GREATER
    private static readonly Random Randomizer = new Random();
#endif

    public static async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
        Func<Task<HttpResponseMessage>> sendRequestFunc,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int currentDelay = InitialBackoffMilliseconds;
        HttpResponseMessage? response = null;
        while (true)
        {
            try
            {
                if (HasDeadlinePassed(deadline, 0))
                {
                    Logger.LogDebug("Timeout of {Deadline}ms reached, stopping retries", deadline.Millisecond);
                    return response;
                }

                // Attempt to send the http request
                response = await sendRequestFunc();

                // Stop and return the response if the status code is success or there is an unretryable status code.
                if (response.IsSuccessStatusCode || !IsRetryableStatusCode(response.StatusCode))
                {
                    string loggingMessage = response.IsSuccessStatusCode ? $"Logs successfully exported with status code {response.StatusCode}" : $"Logs were not exported with unretryable status code: {response.StatusCode}";
                    Logger.LogInformation(loggingMessage);
                    return response;
                }

                // First check if the backend responds with a retry delay
                TimeSpan? retryAfterDelay = response.Headers.RetryAfter != null ? response.Headers.RetryAfter.Delta : null;

                TimeSpan delayDuration;

                if (retryAfterDelay.HasValue)
                {
                    delayDuration = retryAfterDelay.Value;

                    try
                    {
                        currentDelay = Convert.ToInt32(retryAfterDelay.Value.TotalMilliseconds);
                    }
                    catch (OverflowException)
                    {
                        currentDelay = MaxBackoffMilliseconds;
                    }
                }
                else
                {
                    // If no response for delay from backend we add our own jitter delay
                    delayDuration = TimeSpan.FromMilliseconds(GetRandomNumber(0, currentDelay));
                }

                Logger.LogDebug("Logs were not exported with status code: {StatusCode}. Checking to see if retryable again after: {DelayMilliseconds} ms", response.StatusCode, delayDuration.Milliseconds);

                // If delay exceeds deadline. We drop the http request completely.
                if (HasDeadlinePassed(deadline, delayDuration.Milliseconds))
                {
                    Logger.LogDebug("Timeout will be reached after {Delay}ms delay. Dropping logs with status code {StatusCode}.", delayDuration.Milliseconds, response.StatusCode);
                    return response;
                }

                currentDelay = CalculateNextRetryDelay(currentDelay);
                await Task.Delay(delayDuration);
            }
            catch (Exception e)
            {
                string exceptionName = e.GetType().Name;
                var delayDuration = TimeSpan.FromMilliseconds(GetRandomNumber(0, currentDelay));

                // Handling exceptions. Same logic, we retry with custom jitter delay until it succeeds. If it fails by the time deadline is reached we drop the request completely.
                if (!HasDeadlinePassed(deadline, 0))
                {
                    currentDelay = CalculateNextRetryDelay(currentDelay);
                    if (!HasDeadlinePassed(deadline, delayDuration.Milliseconds))
                    {
                        Logger.LogDebug("{@ExceptionMessage}. Retrying again after {@Delay}ms", exceptionName, delayDuration.Milliseconds);

                        await Task.Delay(delayDuration);
                        continue;
                    }
                }

                Logger.LogDebug("Timeout will be reached after {Delay}ms delay. Dropping logs with exception: {@ExceptionMessage}", delayDuration.Milliseconds, e);
                throw;
            }
        }
    }

    private static bool HasDeadlinePassed(DateTime deadline, double delayDuration)
    {
        return DateTime.UtcNow.AddMilliseconds(delayDuration) >=
        deadline.Subtract(TimeSpan.FromMilliseconds(BufferWindow));
    }

    private static int GetRandomNumber(int min, int max)
    {
#if NET6_0_OR_GREATER
        return Random.Shared.Next(min, max);
#else
        lock (Randomizer)
        {
            return Randomizer.Next(min, max);
        }
#endif
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        switch (statusCode)
        {
#if NETSTANDARD2_1_OR_GREATER || NET
            case HttpStatusCode.TooManyRequests:
#else
            case (HttpStatusCode)429:
#endif
            case HttpStatusCode.BadGateway:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.GatewayTimeout:
                return true;
            default:
                return false;
        }
    }

    private static int CalculateNextRetryDelay(int currentDelayMs)
    {
        var nextDelay = currentDelayMs * BackoffMultiplier;
        return Convert.ToInt32(Math.Min(nextDelay, MaxBackoffMilliseconds));
    }
}
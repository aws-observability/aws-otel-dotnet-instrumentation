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
using AWS.OpenTelemetry.Exporter.Otlp.Udp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Internal;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;

#pragma warning disable CS1700 // Assembly reference is invalid and cannot be resolved
[assembly: InternalsVisibleTo("AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests, PublicKey=6ba7de5ce46d6af3")]

/// <summary>
/// This exporter OVERRIDES the Export functionality of the http/protobuf OtlpTraceExporter to allow spans to be exported
/// to the XRay OTLP endpoint https://xray.[AWSRegion].amazonaws.com/v1/traces. Utilizes the AWSSDK
/// library to sign and directly inject SigV4 Authentication to the exported request's headers.
///
/// NOTE: In order to properly configure the usage of this exporter. Please make sure you have the
/// following environment variables:
///
///     export OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://xray.[AWSRegion].amazonaws.com/v1/traces
///     export OTEL_AWS_SIG_V4_ENABLED=true
///     export OTEL_TRACES_EXPORTER=none
///
/// </summary>
/// <remarks>
/// For more information, see AWS documentation on CloudWatch OTLP Endpoint.
/// </remarks>
public class OtlpAwsSpanExporter : BaseExporter<Activity>
#pragma warning restore CS1700 // Assembly reference is invalid and cannot be resolved
{
    private static readonly string ServiceName = "XRay";
    private static readonly string ContentType = "application/x-protobuf";
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpAwsSpanExporter>();
    private readonly HttpClient client = new HttpClient();
    private readonly Uri endpoint;
    private readonly string region;
    private readonly int timeout;
    private readonly Resource processResource;
    private IAwsAuthenticator authenticator;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsSpanExporter"/> class.
    /// </summary>
    /// <param name="options">OpenTelemetry Protocol (OTLP) exporter options.</param>
    /// <param name="processResource">Otel Resource Object</param>
    public OtlpAwsSpanExporter(OtlpExporterOptions options, Resource processResource)
        : this(options, processResource, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsSpanExporter"/> class.
    /// </summary>
    /// <param name="options">OpenTelemetry Protocol (OTLP) exporter options.</param>
    /// <param name="processResource">Otel Resource Object</param>
    /// <param name="authenticator">The authentication used to sign the request with SigV4</param>
    internal OtlpAwsSpanExporter(OtlpExporterOptions options, Resource processResource, IAwsAuthenticator? authenticator = null)
    {
        this.endpoint = options.Endpoint;
        this.timeout = options.TimeoutMilliseconds;

        // Verified in Plugin.cs that the endpoint matches the XRay endpoint format.
        this.region = this.endpoint.AbsoluteUri.Split('.')[1];
        this.processResource = processResource;
        this.authenticator = authenticator == null ? new DefaultAwsAuthenticator() : authenticator;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        using IDisposable scope = SuppressInstrumentationScope.Begin();

        byte[]? serializedSpans = OtlpExporterUtils.SerializeSpans(batch, this.processResource);

        if (serializedSpans == null)
        {
            Logger.LogError("Null spans cannot be serialized");
            return ExportResult.Failure;
        }

        try
        {
            HttpResponseMessage? message = Task.Run(() =>
             {
                 // The retry delay cannot exceed the configured timeout period for otlp exporter.
                 // If the backend responds with `RetryAfter` duration that would result in exceeding the configured timeout period
                 // we would fail and drop the data.
                 return RetryHelper.ExecuteWithRetryAsync(() => this.InjectSigV4AndSendAsync(serializedSpans), TimeSpan.FromMilliseconds(this.timeout));
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

    private async Task<HttpResponseMessage> InjectSigV4AndSendAsync(byte[] serializedSpans)
    {
        HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, this.endpoint.AbsoluteUri);
        IRequest sigV4Request = await this.GetSignedSigV4Request(serializedSpans);

        sigV4Request.Headers.Remove("content-type");
        sigV4Request.Headers.Add("User-Agent", GetUserAgentString());

        foreach (var header in sigV4Request.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var content = new ByteArrayContent(serializedSpans);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);

        httpRequest.Method = HttpMethod.Post;
        httpRequest.Content = content;

        return await this.client.SendAsync(httpRequest);
    }

    private async Task<IRequest> GetSignedSigV4Request(byte[] content)
    {
        IRequest request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), ServiceName)
        {
            HttpMethod = "POST",
            ContentStream = new MemoryStream(content),
            Endpoint = this.endpoint,
        };

        request.Headers.Add("Host", this.endpoint.Host);
        request.Headers.Add("content-type", ContentType);

        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync();

        AmazonXRayConfig config = new AmazonXRayConfig()
        {
            AuthenticationRegion = this.region,
            UseHttp = false,
            ServiceURL = this.endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
        };

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

    // This is to ensure there is no flakiness with the number of times spans are exported in the retry window. Not part of the upstream's implementation
    private const int BufferWindow = 20;
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<RetryHelper>();

#if !NET6_0_OR_GREATER
    private static readonly Random Randomizer = new Random();
#endif

    // A helper method to continuously retry the given HttpRequest function sender until it:
    // 1. There is a success status code
    // 2. Fails with an unretryable status code.
    // 3. Hits the given deadline time
    // Implementation is based on upstream's ExportClient retryable code:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L130
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
                    Logger.LogInformation("Timeout of {Deadline}ms reached, stopping retries", deadline.Millisecond);
                    return response;
                }

                // Attempt to send the http request
                response = await sendRequestFunc();

                // Stop and return the response if the status code is success or there is an unretryable status code.
                if (response.IsSuccessStatusCode || !IsRetryableStatusCode(response.StatusCode))
                {
                    string loggingMessage = response.IsSuccessStatusCode ? $"Spans successfully exported with status code {response.StatusCode}" : $"Spans were not exported with unretryable status code: {response.StatusCode}";
                    Logger.LogInformation(loggingMessage);
                    return response;
                }

                // First check if the backend responds with a retry delay
                TimeSpan? retryAfterDelay = response.Headers.RetryAfter != null ? response.Headers.RetryAfter.Delta : null;

                TimeSpan delayDuration;

                // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L54
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

                Logger.LogInformation("Spans were not exported with status code: {StatusCode}. Checking to see if retryable again after: {DelayMilliseconds} ms", response.StatusCode, delayDuration.Milliseconds);

                // If delay exceeds deadline. We drop the http requesst completely.
                if (HasDeadlinePassed(deadline, delayDuration.Milliseconds))
                {
                    Logger.LogInformation("Timeout will be reached after {Delay}ms delay. Dropping Spans with status code {StatusCode}.", delayDuration.Milliseconds, response.StatusCode);
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
                        Logger.LogInformation("{@ExceptionMessage}. Retrying again after {@Delay}ms", exceptionName, delayDuration.Milliseconds);

                        await Task.Delay(delayDuration);
                        continue;
                    }
                }

                Logger.LogInformation("Timeout will be reached after {Delay}ms delay. Dropping spans with exception: {@ExceptionMessage}", delayDuration.Milliseconds, e);
                throw;
            }
        }
    }

    // Subtract buffer window from deadline to ensure we have enough of a buffer room to
    // prevent flakiness with exporting spans too close to the deadline
    private static bool HasDeadlinePassed(DateTime deadline, double delayDuration)
    {
        return DateTime.UtcNow.AddMilliseconds(delayDuration) >=
        deadline.Subtract(TimeSpan.FromMilliseconds(BufferWindow));
    }

    // Gets a random number to calculate the next delay before retrying.
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L246
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

    // Is the status code a retryable request?
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L228
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

    // Calculates the next delay before retrying to send the request again using the BackoffMultiplier
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExportClient/OtlpRetry.cs#L192
    private static int CalculateNextRetryDelay(int currentDelayMs)
    {
        var nextDelay = currentDelayMs * BackoffMultiplier;
        return Convert.ToInt32(Math.Min(nextDelay, MaxBackoffMilliseconds));
    }
}

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;

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
{
    private static readonly string ServiceName = "XRay";
    private static readonly string ContentType = "application/x-protobuf";
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpAwsSpanExporter>();
    private readonly HttpClient client = new HttpClient();
    private readonly Uri endpoint;
    private readonly string region;
    private readonly Resource processResource;
    private readonly CancellationTokenSource token;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsSpanExporter"/> class.
    /// </summary>
    /// <param name="options">OpenTelemetry Protocol (OTLP) exporter options.</param>
    /// <param name="processResource">Otel Resource Object</param>
    public OtlpAwsSpanExporter(OtlpExporterOptions options, Resource processResource)
    {
        this.endpoint = options.Endpoint;
        this.token = new CancellationTokenSource(options.TimeoutMilliseconds);

        // Verified in Plugin.cs that the endpoint matches the XRay endpoint format.
        this.region = this.endpoint.AbsoluteUri.Split('.')[1];
        this.processResource = processResource;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        using IDisposable scope = SuppressInstrumentationScope.Begin();

        HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, this.endpoint.AbsoluteUri);
        byte[]? serializedSpans = OtlpExporterUtils.SerializeSpans(batch, this.processResource);

        if (serializedSpans == null)
        {
            Logger.LogError("Null spans cannot be serialized");
            return ExportResult.Failure;
        }

        try
        {
            IRequest sigV4Headers = Task.Run(() =>
            {
                return this.GetSignedSigV4Request(serializedSpans);
            }).GetAwaiter().GetResult();

            sigV4Headers.Headers.Remove("content-type");
            sigV4Headers.Headers.Add("User-Agent", GetUserAgentString());

            foreach (var header in sigV4Headers.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var content = new ByteArrayContent(serializedSpans);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);

            httpRequest.Method = HttpMethod.Post;
            httpRequest.Content = content;

            var response = this.client.SendAsync(httpRequest).Result;

            if (!response.IsSuccessStatusCode) {
                Logger.LogError("Failed to export spans: " + response.ReasonPhrase);
                return ExportResult.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export spans: " + ex.Message);
            return ExportResult.Failure;
        }

        return ExportResult.Success;
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        this.token.Cancel();
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

        ImmutableCredentials credentials = await FallbackCredentialsFactory.GetCredentials().GetCredentialsAsync();

        AWS4Signer signer = new AWS4Signer();

        AmazonXRayConfig config = new AmazonXRayConfig()
        {
            AuthenticationRegion = this.region,
            UseHttp = false,
            ServiceURL = this.endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
        };

        signer.Sign(request, config, null, credentials);

        return request;
    }

    private class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}

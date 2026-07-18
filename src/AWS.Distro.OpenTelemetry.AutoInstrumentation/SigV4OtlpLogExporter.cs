// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// OTLP log exporter with SigV4 signing for CloudWatch Logs endpoint.
/// </summary>
internal class SigV4OtlpLogExporter : BaseExporter<LogRecord>
{
    private static readonly string ServiceName = "logs";
    private static readonly int DefaultTimeoutMilliseconds = 10000;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string region;
    private readonly Dictionary<string, string> customHeaders;
    private Resource? resource;

    public SigV4OtlpLogExporter(Uri endpoint, string region, Dictionary<string, string> customHeaders)
    {
        this.endpoint = endpoint;
        this.region = region;
        this.customHeaders = customHeaders;
        this.client = new HttpClient()
        {
            Timeout = TimeSpan.FromMilliseconds(GetTimeoutMilliseconds()),
        };
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        using var scope = SuppressInstrumentationScope.Begin();

        if (this.resource == null && this.ParentProvider is LoggerProvider provider)
        {
            this.resource = provider.GetResource() ?? Resource.Empty;
        }

        byte[] serializedData = new byte[750000];
        int length = OtlpExporterUtils.WriteLogsData(ref serializedData, 0, this.resource, batch);
        if (length <= 0)
        {
            return ExportResult.Failure;
        }

        try
        {
            IRequest sigV4Request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), ServiceName)
            {
                HttpMethod = "POST",
                ContentStream = new MemoryStream(serializedData, 0, length),
                Endpoint = this.endpoint,
                SignatureVersion = SignatureVersion.SigV4,
            };

            var config = new AmazonXRayConfig()
            {
                AuthenticationRegion = this.region,
                AuthenticationServiceName = ServiceName,
                UseHttp = false,
                ServiceURL = this.endpoint.AbsoluteUri,
                RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
            };

#pragma warning disable CS0618 // FallbackCredentialsFactory is obsolete in v4 but still functional
            var awsCredentials = FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618
            var immutableCredentials = awsCredentials.GetCredentialsAsync().GetAwaiter().GetResult();

            if (immutableCredentials.UseToken && immutableCredentials.Token != null)
            {
                sigV4Request.Headers.Add("x-amz-security-token", immutableCredentials.Token);
            }

            sigV4Request.Headers.Add("Host", this.endpoint.Host);
            sigV4Request.Headers.Add("content-type", "application/x-protobuf");

            foreach (var header in this.customHeaders)
            {
                sigV4Request.Headers.Add(header.Key, header.Value);
            }

            new AWS4Signer().Sign(sigV4Request, config, null, awsCredentials);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, this.endpoint);
            foreach (var header in sigV4Request.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            httpRequest.Content = new ByteArrayContent(serializedData, 0, length);
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

            var response = this.client.SendAsync(httpRequest).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? ExportResult.Success : ExportResult.Failure;
        }
        catch
        {
            return ExportResult.Failure;
        }
    }

    private static int GetTimeoutMilliseconds()
    {
        string? timeout = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_TIMEOUT");
        if (int.TryParse(timeout, out int result))
        {
            return result;
        }

        return DefaultTimeoutMilliseconds;
    }

    private class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}

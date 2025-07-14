// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.Net.Http;
using Amazon.S3;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace integration_test_app.Controllers;

[ApiController]
[Route("[controller]")]
public class AppController : ControllerBase
{
    private readonly AmazonS3Client s3Client = new AmazonS3Client();
    private readonly HttpClient httpClient = new HttpClient();
    private readonly ILogger<AppController> _logger;  // Add this

    // Add constructor
    public AppController(ILogger<AppController> logger)
    {
        _logger = logger;
        _logger.LogInformation("AppController initialized");
        _logger.LogInformation($"OTLP Logs Endpoint: {Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")}");
        _logger.LogInformation($"AWS SigV4 Enabled: {Environment.GetEnvironmentVariable("OTEL_AWS_SIG_V4_ENABLED")}");
        _logger.LogInformation($"Log Headers: {Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_HEADERS")}");
    }

    [HttpGet]
    [Route("/outgoing-http-call")]
    public string OutgoingHttp()
    {
        _logger.LogInformation("Making outgoing HTTP call to aws.amazon.com");
        _ = this.httpClient.GetAsync("https://aws.amazon.com").Result;
        _logger.LogInformation("HTTP call completed");

        return this.GetTraceId();
    }

    [HttpGet]
    [Route("/aws-sdk-call")]
    public string AWSSDKCall()
    {
        _logger.LogInformation("Making AWS SDK call to list buckets");
        _ = this.s3Client.ListBucketsAsync().Result;
        _logger.LogInformation("AWS SDK call completed");

        return this.GetTraceId();
    }

    [HttpGet]
    [Route("/")]
    public string Default()
    {
        _logger.LogInformation("Default endpoint called");
        return "Application started!";
    }

    [HttpGet]
    [Route("/test-logging")]
    public IActionResult TestLogging()
    {
        try
        {
            _logger.LogInformation("Test log message with timestamp: {Time}", DateTimeOffset.UtcNow);
            _logger.LogWarning("Test warning message");
            _logger.LogError("Test error message");
            return Ok("Logging test completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logging test");
            return StatusCode(500, "Logging test failed");
        }
    }

    private string GetTraceId()
    {
        var traceId = Activity.Current.TraceId.ToHexString();
        var version = "1";
        var epoch = traceId.Substring(0, 8);
        var random = traceId.Substring(8);
        _logger.LogInformation($"Generated trace ID: {version}-{epoch}-{random}");
        return "{" + "\"traceId\"" + ": " + "\"" + version + "-" + epoch + "-" + random + "\"" + "}";
    }
}
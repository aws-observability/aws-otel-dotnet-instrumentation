// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using Amazon.S3;
using Microsoft.AspNetCore.Mvc;

namespace integration_test_app.Controllers;

[ApiController]
[Route("[controller]")]
public class AppController : ControllerBase
{
    private readonly AmazonS3Client s3Client = new AmazonS3Client();
    private readonly HttpClient httpClient = new HttpClient();

    [HttpGet]
    [Route("/outgoing-http-call")]
    public string OutgoingHttp()
    {
        _ = this.httpClient.GetAsync("https://aws.amazon.com").Result;

        return this.GetTraceId();
    }

    [HttpGet]
    [Route("/aws-sdk-call")]
    public string AWSSDKCall()
    {
        _ = this.s3Client.ListBucketsAsync().Result;

        return this.GetTraceId();
    }

    [HttpGet]
    [Route("/")]
    public string Default()
    {
        return "Application started!";
    }

    private string GetTraceId()
    {
        var traceId = Activity.Current.TraceId.ToHexString();
        var version = "1";
        var epoch = traceId.Substring(0, 8);
        var random = traceId.Substring(8);
        return "{" + "\"traceId\"" + ": " + "\"" + version + "-" + epoch + "-" + random + "\"" + "}";
    }
}

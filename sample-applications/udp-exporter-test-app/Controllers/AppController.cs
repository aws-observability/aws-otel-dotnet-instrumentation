using System;
using Amazon.S3;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.AspNetCore.Http.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Instrumentation;
using System.Diagnostics.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace dotnet_sample_app.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AppController : ControllerBase
    {
        public static readonly ActivitySource tracer = new(
        "dotnet-sample-app");

        public AppController() {}

        [HttpGet]
        [Route("/test")]
        public string TestUdpExporter()
        {
            using var activity = tracer.StartActivity("test_parent_span");
            activity?.SetTag("language", "dotnet");
            activity?.SetTag("signal", "trace");
            activity?.SetTag("test.attribute", "test_value");

            return GetTraceId();
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
            return version + "-" + epoch + "-" + random;
        }
    }
}

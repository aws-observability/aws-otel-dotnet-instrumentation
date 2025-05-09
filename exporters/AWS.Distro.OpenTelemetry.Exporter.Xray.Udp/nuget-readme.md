# AWS Distro for OpenTelemetry X-Ray UDP Exporter
The AWS Distro for OpenTelemetry X-Ray UDP Exporter allows you to send OpenTelemetry traces to the AWS X-Ray daemon over UDP.

## Installation
```console
dotnet add package AWS.Distro.OpenTelemetry.Exporter.Xray.Udp
```

## Prerequisites
- .NET 6.0 or higher

## Usage Example
```c
var resourceBuilder = ResourceBuilder.CreateDefault().AddTelemetrySdk();

Sdk.CreateTracerProviderBuilder()
    .AddSource("dotnet-sample-app")
    .SetResourceBuilder(resourceBuilder)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    // Add the X-Ray UDP Exporter
    .AddXrayUdpExporter(resourceBuilder.Build(), "localhost:2000")
    .Build();
```

## ASP.NET Core Integration
```c
// In Program.cs
builder.Services.AddOpenTelemetry()
    .WithTracing(builder =>
     {
         var resourceBuilder = ResourceBuilder.CreateDefault()
         .AddService("my-service")
         .AddTelemetrySdk();

         builder
         .SetResourceBuilder(resourceBuilder)
         .AddAspNetCoreInstrumentation()
         .AddXrayUdpExporter(resourceBuilder.Build(), "localhost:2000");
     });
```

## License
This project is licensed under the Apache-2.0 License.

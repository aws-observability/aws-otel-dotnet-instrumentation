# AWS Distro for OpenTelemetry X-Ray UDP Exporter
The AWS Distro for OpenTelemetry X-Ray UDP Exporter allows you to send OpenTelemetry traces to the AWS X-Ray Daemon endpoint in Lambda environments over UDP.

## Installation
```console
dotnet add package AWS.Distro.OpenTelemetry.Exporter.Xray.Udp
```

## Prerequisites
- .NET 8.0 or higher

## Usage Example
```c#
    // AWS_LAMBDA_FUNCTION_NAME Environment Variable will be defined in AWS Lambda Environment
    private static String serviceName = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
    private static ResourceBuilder resourceBuilder = ResourceBuilder
        .CreateDefault()
        .AddService(serviceName);

    TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
        .AddAWSLambdaConfigurations()
        .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
        .AddAWSInstrumentation()
        .AddProcessor(
            new SimpleActivityExportProcessor(
                // Add the X-Ray UDP Exporter
                new XrayUdpExporter(resourceBuilder.Build())
            )
        )
        .Build();
```

## License
This project is licensed under the Apache-2.0 License.

# AWS Distro for OpenTelemetry - Instrumentation for DotNet

## Introduction

This project is a redistribution of the [OpenTelemetry Agent for DotNet](https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation),
preconfigured for use with AWS services. Please check out that project too to get a better
understanding of the underlying internals. You won't see much code in this repository since we only
apply some small configuration changes, and our OpenTelemetry friends takes care of the rest.

## Prerequisites

1. Having one of [VSCode](https://code.visualstudio.com/docs/languages/dotnet), Visual Studio, or ReSharper installed
2. Having dotnet cli installed.

## Important

Although this repository is released under the Apache-2.0 license, some Dockerfiles uses Windows as a base image, which is licensed under the following terms https://learn.microsoft.com/en-us/virtualization/windowscontainers/images-eula.

## Building the Project

### Building Locally

To build the dll files for the `AWS.Distro.OpenTelemetry.AutoInstrumentation` project, just run

```sh
dotnet build
```

This will build the dll under `src/AWS.Distro.OpenTelemetry.AutoInstrumentation/bin/Debug/net8.0`.

### Building For Release

To build the project for release, packaged with OpenTelemetry DotNet Instrumentation, run the following in the root directory:

```sh
bash build.sh
```

This runs build/Build.cs which basically pulls OpenTelemetry DotNet Instrumentation packaged zip file and adds the `AWS.Distro.OpenTelemetry.AutoInstrumentation` dll there.

## Styling

This package uses [StyleCop](https://github.com/DotNetAnalyzers/StyleCopAnalyzers) to enforce styling rules as well as having Copyright headers. More information about the rules themselves can be found [here](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/DOCUMENTATION.md). The project is configured to use StyleCop so any file that is added that violates any of the rules, the Code Editor will complain. Alternatively, running `dotnet build` will complain as well. Unfortunately, it doesn't auto apply styling. To fix "most" of styling issues that come up, you can run `dotnet format`.

## Testing

For Integration Testing, follow README under `sample-applications/integration-test-app`

For Unit Testing, followed this [doc](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test) to create test/AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests directory. You can use that doc to add more unit tests. To run the unit tests, run `dotnet test`

## Security

See [CONTRIBUTING](CONTRIBUTING.md#security-issue-notifications) for more information.

## License

This project is licensed under the Apache-2.0 License.

## Checksum Verification

Artifacts released will include a `.sha256` file for checksum verification starting from v1.32.6
To verify, run the command `shasum -a 256 -c <artifact_name>.sha256` 
It should return the output `<artifact_name>: OK` if the validation is successful


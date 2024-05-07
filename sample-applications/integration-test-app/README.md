# AWS Distro for OpenTelemetry .NET - Integration Testing App

This application was copied from: <https://github.com/aws-observability/aws-otel-dotnet/tree/main/integration-test-app>

This application will be used to validate the continual integration with the AWS OTEL AutoInstrumentation .NET and the AWS Application Signals back-end service.

## Application interface

The application exposes the following routes:

1. `/`
    - Ensures the application is running.
2. `/outgoing-http-call`
    - Makes a HTTP request to `aws.amazon.com`.
3. `/aws-sdk-call`
    - Makes a call to AWS S3 to list buckets for the account corresponding to the provided AWS credentials.

## Running the integration testing application locally

If you want to run it locally, follow steps below:

1. Ensure that you have AWS credentials [configured](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-quickstart.html).

    `note`: Windows users will need to change the the volume mount source path for AWS credentials from `~/.aws` to `%USERPROFILE%\.aws`

    ```shell
    docker build -t aspnetapp .
    docker-compose up
    ```

2. Run `bash build-and-start-application.sh` in the sample app directory. This will build the distribution, copy over the instrumentation dlls and start up 2 docker containers: One for the integration test application and one for the collector (NEEDS TO BE FIXED).

3. Visit the following endpoints when containers start:

    `localhost:8080/aws-sdk-call` and `localhost:8080/outgoing-http-call`

You should be able to see traces in X-Ray console in your account(`us-west-2`).

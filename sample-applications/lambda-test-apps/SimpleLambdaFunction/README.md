# AWS Lambda Application Signals Support

This package provides support for **Application Signals** in AWS Lambda environment.

## Features

- Supports Application Signals, including traces and metrics, for AWS Lambda .NET Runtimes.
- Automates the deployment process, including the creation of the Application .NET Lambda Layer and a sample Lambda function.

## Prerequisites

Before you begin, make sure you have installed and configured the following tools:

- [Docker](https://www.docker.com/get-started)
- [Terraform](https://www.terraform.io/downloads)
- [AWS CLI](https://aws.amazon.com/cli/) (to configure AWS credentials)
- [.NET Lambda Tools](https://docs.aws.amazon.com/lambda/latest/dg/csharp-package-cli.html) (to build and publish lambda function)

### Configure AWS Credentials

Ensure that your AWS credentials are properly configured in your local environment. You can use the following command with the AWS CLI:

```bash
aws configure
```
This will prompt you to enter your `AWS Access Key ID`, `AWS Secret Access Key`, `Default region name`, and `Default output format`.

## Installation and Deployment

### 1. Clone the Repository

First, clone this repository to your local machine:

```bash
git clone https://github.com/aws-observability/aws-otel-dotnet-instrumentation.git
```

### 2. Run the Build Script

Navigate to the `lambda-test-apps` folder and run the `build-and-deploy.sh` script. This will create the Application .NET Lambda Layer and a Lambda sample app in your AWS account:

```bash
cd sample-applications/lambda-test-apps/SimpleLambdaFunction/
./build-and-deploy.sh
```

## Lambda Sample App

Once the script has successfully run, you will see the deployed Lambda sample app in your AWS account. You can trigger the 
Lambda function and view the traces and metrics through the AWS CloudWatch Console.
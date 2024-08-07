# AppSignals.NetCore

This project is a sample .Net 8.0 Web API that integrates with Dynamo DB through
the official AWS SDK for .Net

## Prerequisites

- [Docker](https://www.docker.com/get-started) installed on your machine.
- A Dynamo DB table named `TodoItems` in your account with `Id` as the sort key.
- AWS credentials with read and write permission to the `TodoItems` Dynamo DB
  table.

## Building the docker image

Follow the steps below to build the Docker image

1. Clone the repository

```sh
git clone ssh://git.amazon.com/pkg/Dotnet-appsignals-demo
```

2. Navigate to the project folder

```sh
cd your-repo/app/AppSignals.NetCore
```

3. Build the Docker image

```sh
docker build -t appsignals-netcore .
```

## Running the docker container

Execute the command below with the appropriate `AWS_REGION` and AWS credentials
from the `Prerequisites` section to run the docker container.

```sh
docker run -e AWS_REGION=<your-region> -e AWS_ACCESS_KEY_ID=<your-access-key-id> -e AWS_SECRET_ACCESS_KEY=<your-secret-access-key> -e AWS_SESSION_TOKEN=<your-session-token> -p 8000:8080 appsignals-netcore
```

Navigate to http://localhost:8000/swagger/index.html to access the API's Swagger
definition and Test harness to test the API.
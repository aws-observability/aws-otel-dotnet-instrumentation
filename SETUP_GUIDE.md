# Enabling Application Signals for .NET with OpenTelemetry Auto-Instrumentation

## Introduction
This guide provides step-by-step instructions for enabling Application Signals in .NET applications using the AWS Distro for OpenTelemetry (ADOT) Instrumentation for .NET on supported platforms.
* The [Integration Test Application]((https://github.com/aws-observability/aws-otel-dotnet-instrumentation/tree/main/sample-applications/integration-test-app)) is used as the reference .NET application throughout this guide. to demonstrates how to implement and test the ADOT instrumentation
* The CloudWatch Agent is set up to collect OTel telemetry data emitted by the application, and then sent to Amazon CloudWatch for monitoring and analysis
* The guide provisions all necessary infrastructure to run the sample application from scratch

## EC2 Linux
### Prerequisites
* Launch a Linux EC2 instance
  * Specify a key pair (.pem) for secure connection to the instance
  * Add [CloudWatchAgentServerPolicy](https://docs.aws.amazon.com/aws-managed-policy/latest/reference/CloudWatchAgentServerPolicy.html) to instance IAM role
* Log into the instance using SSH client
  ```sh
  chmod 400 "<customer_key_pair_name>.pem"
  ssh -i "<customer_key_pair_name>.pem" ec2-user@ec2-XX-XXX-XXX-XXX.compute-1.amazonaws.com
  ```
* Install .NET 8
  ```sh
  sudo rpm --import https://packages.microsoft.com/keys/microsoft.asc
  sudo wget -O /etc/yum.repos.d/microsoft-prod.repo https://packages.microsoft.com/config/fedora/37/prod.repo
  sudo dnf install -y dotnet-sdk-8.0
  dotnet --version > /tmp/dotnet-version
  ```
* Install AWS CLI
   * AWS CLI is preinstalled on Amazon Linux instances, verify by
     ```sh
     aws --version
     ```
   * If not installed, follow [AWS CLI Installation Guide](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html#getting-started-install-instructions) to install
* Install Git
  ```sh
  sudo yum update -y
  sudo yum install git -y
  git --version
  ```
### Set up CloudWatch Agent with Application Signals enabled
* [Enable Application Signals in your account](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Application-Signals-Enable-EC2.html#CloudWatch-Application-Signals-EC2-Grant)
* [Download and start the CloudWatch agent](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Application-Signals-Enable-EC2.html#CloudWatch-Application-Signals-Enable-Other-agent)

### Download AWS Distro of OTel .NET auto-instrumentation agent
```sh
wget https://github.com/aws-observability/aws-otel-dotnet-instrumentation/releases/latest/download/aws-distro-opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip
export INSTALL_DIR=~/OpenTelemetryDistribution
unzip AWS-opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip -d $INSTALL_DIR
```

### Run the sample app with .NET auto-instrumentation agent
* Pull the agent repo from GitHub to use the integration test application
  ```sh
  git clone https://github.com/aws-observability/aws-otel-dotnet-instrumentation
  ```
* Build the sample application
  ```sh
  cd aws-otel-dotnet-instrumentation/sample-applications/integration-test-app/
  dotnet publish integration-test-app/integration-test-app.csproj -c Release -o out
  ```
* Run the sample applcation with .NET auto-instrumentation agent
  ```sh
  export INSTALL_DIR=~/OpenTelemetryDistribution
  export CORECLR_ENABLE_PROFILING=1
  export CORECLR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}
  export CORECLR_PROFILER_PATH=${INSTALL_DIR}/linux-x64/OpenTelemetry.AutoInstrumentation.Native.so
  export DOTNET_ADDITIONAL_DEPS=${INSTALL_DIR}/AdditionalDeps
  export DOTNET_SHARED_STORE=${INSTALL_DIR}/store
  export DOTNET_STARTUP_HOOKS=${INSTALL_DIR}/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
  export OTEL_DOTNET_AUTO_HOME=${INSTALL_DIR}

  export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"

  export OTEL_RESOURCE_ATTRIBUTES=service.name=aws-otel-integ-test
  export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
  export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4316
  export OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT=http://127.0.0.1:4316/v1/metrics
  export OTEL_METRICS_EXPORTER=none
  export OTEL_AWS_APPLICATION_SIGNALS_ENABLED=true
  export OTEL_TRACES_SAMPLER=xray
  export OTEL_TRACES_SAMPLER_ARG=http://127.0.0.1:2000


  export ASPNETCORE_URLS=http://+:8080
  export LISTEN_ADDRESS=0.0.0.0:8080

  dotnet out/integration-test-app.dll
  ```
### View the collected OTel telemetry data in the [CloudWatch Application Signals service console](https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#application-signals:services)

## EC2 Windows
### Prerequisites
* Launch a Windows EC2 instance
  * Edit security group inbound rule to allow RDP traffic from your machine
  * Add [CloudWatchAgentServerPolicy](https://docs.aws.amazon.com/aws-managed-policy/latest/reference/CloudWatchAgentServerPolicy.html) to instance IAM role
* Log into the instance using RDP tools
  * Open a PowerShell session
* Install .NET 8
  * In powershell session
    ```ps
    wget -O dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
    .\dotnet-install.ps1 -Version 8.0.302
    ```
* Install AWS CLI
  * Install following [AWS CLI user guide](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
    ```ps
    msiexec.exe /i https://awscli.amazonaws.com/AWSCLIV2.msi
    ```
  * Reopen PowerShell session after around 30 seconds and verify installation
    ```ps
    aws --version
    ```
* Install Git
  ```ps
  winget install --id Git.Git -e --source winget
  $env:PATH += ";C:\Program Files\Git\cmd"
  git --version
  ```

### Set up CloudWatch Agent with Application Signals enabled
* [Enable Application Signals in your account](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Application-Signals-Enable-EC2.html#CloudWatch-Application-Signals-EC2-Grant)
* Download and start the CloudWatch agent
  * Either follow [Linux instruction](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Application-Signals-Enable-EC2.html#CloudWatch-Application-Signals-Enable-Other-agent) and refer to [CW Agent guidance for different processes setting up Windows](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/install-CloudWatch-Agent-commandline-fleet.html)
  * Or follow these steps:
    * Install CloudWatch Agent
      * Download [amazon-cloudwatch-agent.msi](https://amazoncloudwatch-agent.s3.amazonaws.com/windows/amd64/latest/amazon-cloudwatch-agent.msi)
      * Install
        ```ps
        msiexec /i amazon-cloudwatch-agent.msi
        ```
    * Create CloudWatch Agent configuraiton file
      ```ps
      New-Item -ItemType Directory -Path "C:\tmp" -Force

      $content = @"
      {
        "traces": {
          "traces_collected": {
            "application_signals": {}
          }
        },
        "logs": {
          "metrics_collected": {
            "application_signals": {}
          }
        }
      }
      "@

      Set-Content -Path "C:\tmp\application-signals-cwagent-config.txt" -Value $content
      ```
    * Start CloudWatch Agent
      ```ps
      & $Env:ProgramFiles\Amazon\AmazonCloudWatchAgent\amazon-cloudwatch-agent-ctl.ps1 -m ec2 -a status
      ```

### Download AWS Distro of OTel .NET auto-instrumentation agent
```ps
Invoke-WebRequest -Uri "https://github.com/aws-observability/aws-otel-dotnet-instrumentation/releases/latest/download/aws-distro-opentelemetry-dotnet-instrumentation-windows.zip" -OutFile "aws-distro-opentelemetry-dotnet-instrumentation-windows.zip"
$env:INSTALL_DIR = "C:\Users\Administrator\Downloads\OpenTelemetryDistribution"
Expand-Archive -Path "aws-distro-opentelemetry-dotnet-instrumentation-windows.zip" -DestinationPath $env:INSTALL_DIR
```

### Run the sample app with .NET auto-instrumentation agent
* Pull the agent repo from GitHub to use the integration test application
  ```ps
  git clone https://github.com/aws-observability/aws-otel-dotnet-instrumentation
  ```
* Build the sample application
  ```ps
  cd aws-otel-dotnet-instrumentation/sample-applications/integration-test-app/
  dotnet publish integration-test-app/integration-test-app.csproj -c Release -o out
  ```
* Run the sample applcation with .NET auto-instrumentation agent
  ```ps
  $env:INSTALL_DIR = "C:\Users\Administrator\Downloads\OpenTelemetryDistribution"
  $env:CORECLR_ENABLE_PROFILING = "1"
  $env:CORECLR_PROFILER = "{918728DD-259F-4A6A-AC2B-B85E1B658318}"
  $env:CORECLR_PROFILER_PATH = "$env:INSTALL_DIR\win-x64\OpenTelemetry.AutoInstrumentation.Native.dll"
  $env:DOTNET_ADDITIONAL_DEPS = "$env:INSTALL_DIR\AdditionalDeps"
  $env:DOTNET_SHARED_STORE = "$env:INSTALL_DIR\store"
  $env:DOTNET_STARTUP_HOOKS = "$env:INSTALL_DIR\net\OpenTelemetry.AutoInstrumentation.StartupHook.dll"
  $env:OTEL_DOTNET_AUTO_HOME = "$env:INSTALL_DIR"
  $env:OTEL_DOTNET_AUTO_PLUGINS = "AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
  $env:OTEL_RESOURCE_ATTRIBUTES = "service.name=aws-otel-integ-test"
  $env:OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf"
  $env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4316"
  $env:OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT = "http://127.0.0.1:4316/v1/metrics“
  $env:OTEL_METRICS_EXPORTER = "none"
  $env:OTEL_AWS_APPLICATION_SIGNALS_ENABLED = "true"
  $env:OTEL_TRACES_SAMPLER = "xray"
  $env:OTEL_TRACES_SAMPLER_ARG = "http://127.0.0.1:2000"
  $env:ASPNETCORE_URLS = "http://+:8080"
  $env:LISTEN_ADDRESS = "0.0.0.0:8080"

  dotnet out/integration-test-app.dll
  ```

### View the collected OTel telemetry data in the [CloudWatch Application Signals service console](https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#application-signals:services)

## EKS Linux
### Prerequisites
* [Create VPC for EKS cluster](https://docs.aws.amazon.com/eks/latest/userguide/creating-a-vpc.html)
    * Choose IPv4 CloudFormation template for easier setup
* Install ```kubectl```
  ```sh
  curl -O https://s3.us-west-2.amazonaws.com/amazon-eks/1.30.0/2024-05-12/bin/darwin/amd64/kubectl
  ```
* Install AWS CLI
    * Install following [AWS CLI user guide](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
* Install ```eksctl```
  ```sh
  brew tap weaveworks/tap
  brew install weaveworks/tap/eksctl
  ```
* Create EKS Cluster
    * Specify ```--vpc-private-subnets``` to be the private subnets created in the VPC CloudFormation stack
  ```sh
  eksctl create cluster --name <my-cluster-name> --region <region-code> --version 1.30 --vpc-private-subnets subnet-ID1,subnet-ID2 --without-nodegroup
  ```

### Launch EKS Worker Nodes
* Launch Linux Workder Nodes
  * Both the sample application and ```amazon-cloudwatch-observability``` addon run on pods on these Linux nodes
  * Specify ```--subnet-ids``` to be the public subnets created in the VPC CloudFormation stack
    ```sh
    eksctl create nodegroup --cluster <my-cluster-name> --name <my-nodegroup-name> --node-type m5.large --nodes 3 --nodes-min 1 --nodes-max 4 --ssh-access --managed --region <region-code> --ssh-public-key <my-public-key-name> --subnet-ids subnet-ID1,subnet-ID2
    ```
  * Add managed policy ```CloudWatchAgentServerPolicy``` to the node role

### Install ```amazon-cloudwatch-observability``` addon
* Install addon
  ```sh
  aws eks create-addon --cluster-name <my-cluster-name> --addon-name amazon-cloudwatch-observability
  ```
* Verify deployment configuration of the addon
  ```sh
  kubectl edit deployment amazon-cloudwatch-observability-controller-manager -n amazon-cloudwatch
  ```
  * Verify ADOT auto-instrumentation image is specified
    ```sh
    spec:
      containers:
      - args:
        ....
        - --auto-instrumentation-dotnet-image=public.ecr.aws/aws-observability/adot-autoinstrumentation-dotnet:v1.1.0
    ```

### Build ECR image of sample application
* Pull the agent repo from GitHub to use the integration test application
  ```sh
  git clone https://github.com/aws-observability/aws-otel-dotnet-instrumentation
  ```
* Build the sample application
  ```sh
  cd aws-otel-dotnet-instrumentation/sample-applications/integration-test-app/
  dotnet publish integration-test-app/integration-test-app.csproj -c Release -o out
  ```
* Replace ```Dockerfile``` contents
  ```dockerfile
  FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
  WORKDIR /app
  COPY . ./
  RUN dotnet publish integration-test-app/integration-test-app.csproj -c Release -o out

  FROM mcr.microsoft.com/dotnet/aspnet:8.0
  WORKDIR /app
  EXPOSE 8080
  COPY --from=build-env /app/out .
  ENTRYPOINT ["dotnet", "integration-test-app.dll"]
  ```
* Build docker image for sample applcation
  ```sh
  docker build -t dotnet-sample-app .
  ```
  * Verify ```sample-app``` docker image created
    ```shell
    docker images dotnet-sample-app
    ```
* Authenticate docker to your Amazon ECR Registry
  ```sh
  aws ecr get-login-password --region <region-code> | docker login --username AWS --password-stdin <aws-account-id>.dkr.ecr.<region-code>.amazonaws.com
  ```
* Create a repository in Amazon ECR
  ```sh
  aws ecr create-repository --repository-name dotnet-sample-app-repo --region <region-code>
  ```
* Tag your local docker image with the ECR repository URI
  ```sh
  docker tag dotnet-sample-app:latest <aws-account-id>.dkr.ecr.<region-code>.amazonaws.com/dotnet-sample-app-repo:latest
  ```
* Push the docker image to your ECR repository
  ```sh
  docker push <aws-account-id>.dkr.ecr.<region-code>.amazonaws.com/dotnet-sample-app-repo:latest
  ```

### Run the sample app with .NET auto-instrumentation agent
* Create sample application configuration file ```dotnet-demo.yaml```
  ```sh
  apiVersion: v1
  kind: Namespace
  metadata:
    labels:
      kubernetes.io/metadata.name: linux
    name: dotnet
  ---
  apiVersion: apps/v1
  kind: Deployment
  metadata:
    name: dotnet-demo
    namespace: dotnet
  spec:
    selector:
      matchLabels:
        app: dotnet-demo
        tier: backend
        track: stable
    replicas: 1
    template:
      metadata:
        labels:
          app: dotnet-demo
          tier: backend
          track: stable
        annotations:
          instrumentation.opentelemetry.io/inject-dotnet: "true"
      spec:
        containers:
        - name: dotnet-demo
          image: <aws-account-id>.dkr.ecr.<region-code>.amazonaws.com/dotnet-sample-app-repo:latest
          ports:
          - name: http
            containerPort: 8080
          command: ["dotnet", "integration-test-app.dll"]
          imagePullPolicy: Always
          env:
            - name: AWS_REGION
              value: "us-east-1"
            - name: LISTEN_ADDRESS
              value: "0.0.0.0:8080"
            - name: ASPNETCORE_URLS
              value: "http://+:8080"
        nodeSelector:
          kubernetes.io/os: linux
        tolerations:
            - key: "os"
              operator: "Equal"
              value: "linux"
              effect: "NoSchedule"
  ```
* Run the sample applcation with .NET auto-instrumentation agent
  ```sh
  kubectl apply -f dotnet-demo.yaml
  ```

### View the collected OTel telemetry data in the [CloudWatch Application Signals service console](https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#application-signals:services)

## EKS Windows
### Prerequisites
* Same as [EKS Linux](#eks-linux)

### Launch EKS Worker Nodes
* Launch Linux Workder Nodes
  * ```amazon-cloudwatch-observability``` addon runs on pods on these Linux nodes
  * Specify ```--subnet-ids``` to be the public subnets created in the VPC CloudFormation stack
    ```sh
    eksctl create nodegroup --cluster <my-cluster-name> --name <my-linux-nodegroup-name> --node-type m5.large --nodes 3 --nodes-min 1 --nodes-max 4 --ssh-access --managed --region <region-code> --ssh-public-key <my-public-key-name> --subnet-ids subnet-ID1,subnet-ID2
    ```
  * Add managed policy ```CloudWatchAgentServerPolicy``` to the node role
* Launch Windows Workder Nodes
  * The sample application runs on pods on these Windows nodes
  * Specify ```--subnet-ids``` to be the public subnets created in the VPC CloudFormation stack
    ```sh
    eksctl create nodegroup --cluster <my-cluster-name> --name <my-windows-nodegroup-name> --node-type m5.large --nodes 3 --nodes-min 1 --nodes-max 4 --managed --region <region-code> --node-ami-family WindowsServer2022FullContainer --subnet-ids subnet-ID1,subnet-ID2
    ```
  * Follow EKS guidance to [Enable Windows support](https://docs.aws.amazon.com/eks/latest/userguide/windows-support.html#enable-windows-support)
  * Open Windows Server Firewall rules
    * This is needed since Windows nodes using the CloudWatch Agent as a Host Process Container cannot enable Application Signals due to limitations in Kubernetes networking on Windows
    * Log into each Windows node (using RDP tool, or Systems Manager Session Manager) and run PowerShell command
      ```sh
      netsh advfirewall firewall add rule name="appsignals" dir=in action=allow localport=4316 protocol=tcp
      ```

### Install ```amazon-cloudwatch-observability``` addon
* Same as [EKS Linux](#eks-linux)

### Build ECR image of sample application
* Same as [EKS Linux](#eks-linux), replace ```Dockerfile``` contents with
```sh
FROM mcr.microsoft.com/dotnet/aspnet:8.0-nanoserver-ltsc2022
WORKDIR /app
EXPOSE 8080
COPY out .
ENTRYPOINT ["dotnet", "integration-test-app.dll"]
```

### Run the sample app with .NET auto-instrumentation agent
* Create sample application configuration file ```dotnet-demo-windows.yaml```
  ```sh
  apiVersion: v1
  kind: Namespace
  metadata:
    labels:
      kubernetes.io/metadata.name: windows
    name: dotnet-win
  ---
  apiVersion: apps/v1
  kind: Deployment
  metadata:
    name: dotnet-demo-win
    namespace: dotnet-win
  spec:
    selector:
      matchLabels:
        app: dotnet-demo-win
        tier: backend
        track: stable
    replicas: 1
    template:
      metadata:
        labels:
          app: dotnet-demo-win
          tier: backend
          track: stable
        annotations:
          instrumentation.opentelemetry.io/inject-dotnet: "true"
      spec:
        containers:
        - name: dotnet-demo-win
          image: <aws-account-id>.dkr.ecr.<region-code>.amazonaws.com/dotnet-sample-app-repo:latest
          ports:
          - name: http
            containerPort: 8080
          command: ["dotnet", "integration-test-app.dll"]
          imagePullPolicy: Always
          env:
            - name: AWS_REGION
              value: "us-east-1"
            - name: LISTEN_ADDRESS
              value: "0.0.0.0:8080"
            - name: ASPNETCORE_URLS
              value: "http://+:8080"
        nodeSelector:
          kubernetes.io/os: windows
        tolerations:
            - key: "os"
              operator: "Equal"
              value: "windows"
              effect: "NoSchedule"

  ```
* Run the sample applcation with .NET auto-instrumentation agent
  ```sh
  kubectl apply -f dotnet-demo-windows.yaml
  ```

### View the collected OTel telemetry data in the [CloudWatch Application Signals service console](https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#application-signals:services)

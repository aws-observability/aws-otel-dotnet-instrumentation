locals {
  architecture = var.architecture == "x86_64" ? "x64" : "arm64"
}

resource "aws_lambda_layer_version" "sdk_layer" {
  layer_name          = var.sdk_layer_name
  filename            = "${path.module}/../../../../../bin/aws-distro-opentelemetry-dotnet-instrumentation-linux-glibc-${local.architecture}.zip"
  compatible_runtimes = ["dotnet8"]
  license_info        = "Apache-2.0"
  source_code_hash    = filebase64sha256("${path.module}/../../../../../bin/aws-distro-opentelemetry-dotnet-instrumentation-linux-glibc-${local.architecture}.zip")
}

module "test-function" {
  source  = "terraform-aws-modules/lambda/aws"

  architectures = compact([var.architecture])
  function_name = var.function_name
  handler       = "${var.function_name}::${var.function_name}.Function::FunctionHandler"
  runtime       = var.runtime

  create_package         = false
  local_existing_package = "${path.module}/../../src/SimpleLambdaFunction/bin/Release/net8.0/SimpleLambdaFunction.zip"

  memory_size = 512
  timeout     = 30

  layers = compact([aws_lambda_layer_version.sdk_layer.arn])

  environment_variables = {
    AWS_LAMBDA_EXEC_WRAPPER = "/opt/instrument.sh"
  }

  tracing_mode = var.tracing_mode

  attach_policy_statements = true
  policy_statements = {
    s3 = {
      effect = "Allow"
      actions = [
        "s3:ListAllMyBuckets"
      ]
      resources = [
        "*"
      ]
    }
  }
}

module "api-gateway" {
  source = "../api-gateway-proxy"

  name                = var.function_name
  function_name       = module.test-function.lambda_function_name
  function_invoke_arn = module.test-function.lambda_function_invoke_arn
  enable_xray_tracing = true
}

resource "aws_iam_role_policy_attachment" "hello-lambda-cloudwatch" {
  role       = module.test-function.lambda_function_name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy_attachment" "test_xray" {
  role       = module.test-function.lambda_function_name
  policy_arn = "arn:aws:iam::aws:policy/AWSXRayDaemonWriteAccess"
}
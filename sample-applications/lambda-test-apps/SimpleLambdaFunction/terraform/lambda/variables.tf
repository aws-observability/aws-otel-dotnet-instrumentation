variable "sdk_layer_name" {
  type        = string
  description = "Name of published SDK layer"
  default     = "aws-distro-opentelemetry-dotnet-instrumentation"
}

variable "function_name" {
  type        = string
  description = "Name of sample app function / API gateway"
  default     = "SimpleLambdaFunction"
}

variable "architecture" {
  type        = string
  description = "Lambda function architecture, either arm64 or x86_64"
  default     = "x86_64"
}

variable "runtime" {
  type        = string
  description = ".NET runtime version used for sample Lambda Function"
  default     = "dotnet8"
}

variable "tracing_mode" {
  type        = string
  description = "Lambda function tracing mode"
  default     = "Active"
}

variable "enable_collector_layer" {
  type        = bool
  description = "Enables building and usage of a layer for the collector. If false, it means either the SDK layer includes the collector or it is not used."
  default     = false
}

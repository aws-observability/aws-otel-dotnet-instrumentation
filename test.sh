#!/bin/sh
export OTEL_INSTRUMENTATION_AWS_LAMBDA_HANDLER="$_HANDLER"
export _HANDLER="LambdaWrapper::LambdaWrapper.Function::FunctionHandler"

echo "${@//$OTEL_INSTRUMENTATION_AWS_LAMBDA_HANDLER/$_HANDLER} --additionalprobingpath /opt/LambdaWrapper"

/var/lang/bin/dotnet exec --additionalprobingpath /opt/LambdaWrapper --depsfile /opt/SimpleLambdaFunction/SimpleLambdaFunction.deps.json --runtimeconfig /opt/SimpleLambdaFunction/SimpleLambdaFunction.runtimeconfig.json /var/runtime/Amazon.Lambda.RuntimeSupport.dll SimpleLambdaFunction::SimpleLambdaFunction.Function::FunctionHandler
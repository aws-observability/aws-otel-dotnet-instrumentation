
bash ../../build.sh
rm -rf ./OpenTelemetryDistribution
cp -r ../../OpenTelemetryDistribution .

wget https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/v1.7.0/opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip
mkdir otel-dotnet
unzip opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip -d otel-dotnet

docker build -t performance-test/dotnet-demo -f Dockerfile.perf .

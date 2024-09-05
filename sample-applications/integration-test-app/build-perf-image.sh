
bash ../../build.sh

rm -rf ./OpenTelemetryDistribution
cp -r ../../OpenTelemetryDistribution .

docker build -t performance-test/dotnet-demo -f Dockerfile.perf .


cd ..
./build.sh
cd -

cp -a ../../OpenTelemetryDistribution sample-applications/integration-test-app/
docker build -t performance-test/dotnet-demo -f Dockerfile.perf ../sample-applications/integration-test-app/

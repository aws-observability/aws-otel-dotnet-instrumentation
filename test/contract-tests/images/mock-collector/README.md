### Overview

MockCollector mimics the behaviour of the actual OTEL collector, but stores export requests to be retrieved by contract tests. 

It registers servicers for all three signals: traces, metrics and **logs**. The logs servicer matters for
Dynamic Instrumentation, whose snapshots are emitted as OTLP `LogRecord`s — with no logs servicer registered,
an exporting agent gets `UNIMPLEMENTED: Method not found!` rather than silently recording nothing, so DI could
not be contract-tested at all.

### Protos
To build protos:
1. Run `pip install grpcio grpcio-tools`
2. Change directory to `aws-otel-dotnet-instrumentation/test/contract-tests/images/mock-collector/` 
3. Run: `python -m grpc_tools.protoc -I./protos --python_out=. --pyi_out=. --grpc_python_out=. ./protos/mock_collector_service.proto`
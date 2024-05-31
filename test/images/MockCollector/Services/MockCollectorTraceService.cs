using System.Collections.Concurrent;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace MockCollector.Services;

public class MockCollectorTraceService: TraceService.TraceServiceBase
{
    private readonly ConcurrentQueue<ExportTraceServiceRequest> _exportRequests = new();

    public MockCollectorTraceService()
    {
    }

    public IReadOnlyList<ExportTraceServiceRequest> GetRequests()
    {
        return [.. _exportRequests];
    }

    public void ClearRequests()
    {
        _exportRequests.Clear();
    }

    public void AddTrace(ExportTraceServiceRequest request)
    {
        _exportRequests.Enqueue(request);
    }

    public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
    {
        _exportRequests.Enqueue(request);
        return Task.FromResult(new ExportTraceServiceResponse());
    }
}

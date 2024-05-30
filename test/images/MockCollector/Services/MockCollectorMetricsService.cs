using System.Collections.Concurrent;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace MockCollector.Services;

public class MockCollectorMetricsService: MetricsService.MetricsServiceBase
{
    private readonly ConcurrentQueue<ExportMetricsServiceRequest> _exportRequests = new();
    public MockCollectorMetricsService()
    {
    }

    public IReadOnlyList<ExportMetricsServiceRequest> GetRequests()
    {
        return [.. _exportRequests];
    }

    public void ClearRequests()
    {
        _exportRequests.Clear();
    }

    public override Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
    {
        _exportRequests.Enqueue(request);
        return Task.FromResult(new ExportMetricsServiceResponse());
    }
}

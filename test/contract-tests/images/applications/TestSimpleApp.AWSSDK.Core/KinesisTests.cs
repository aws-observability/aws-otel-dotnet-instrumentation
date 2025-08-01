using System.Text;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class KinesisTests(
    IAmazonKinesis kinesis,
    [FromKeyedServices("fault-kinesis")] IAmazonKinesis faultKinesis,
    [FromKeyedServices("error-kinesis")] IAmazonKinesis errorKinesis,
    ILogger<KinesisTests> logger) : ContractTest(logger)
{
    public Task<CreateStreamResponse> CreateStream()
    {
        return CreateStream("test_stream");
    }

    public Task<CreateStreamResponse> CreateStream(string streamName)
    {
        return kinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = streamName });
    }

    public Task<ListStreamsResponse> ListStreams()
    {
        return kinesis.ListStreamsAsync(new ListStreamsRequest());
    }
    
    public Task<PutRecordResponse> PutRecord()
    {
        // kinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = "test_stream" });
        return kinesis.PutRecordAsync(new PutRecordRequest
        {
            StreamName = "test_stream", Data = new MemoryStream(Encoding.UTF8.GetBytes("test_data")), PartitionKey =
                "partition_key"
        });
    }
    
    public Task<DescribeStreamResponse> DescribeStream()
    {
        return kinesis.DescribeStreamAsync(new DescribeStreamRequest { StreamName = "test-stream", StreamARN = "arn:aws:kinesis:us-east-1:000000000000:stream/test-stream" });
    }

    public Task<DeleteStreamResponse> DeleteStream()
    {
        return kinesis.DeleteStreamAsync(new DeleteStreamRequest { StreamName = "test_stream" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultKinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = "test_stream" });
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorKinesis.DeleteStreamAsync(new DeleteStreamRequest { StreamName = "test_stream_error" });
    }
}

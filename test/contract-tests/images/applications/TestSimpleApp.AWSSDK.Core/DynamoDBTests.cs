using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class DynamoDBTests(
    IAmazonDynamoDB ddb,
    [FromKeyedServices("fault-ddb")] IAmazonDynamoDB faultDdb,
    [FromKeyedServices("error-ddb")] IAmazonDynamoDB errorDdb,
    ILogger<DynamoDBTests> logger) :
    ContractTest(logger)
{
    public Task<CreateTableResponse> CreateTable()
    {
        return CreateTable("test_table");
    }

    public Task<CreateTableResponse> CreateTable(string tableName)
    {
        return ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName, AttributeDefinitions = [new AttributeDefinition { AttributeName = "Id", AttributeType = ScalarAttributeType.S }],
            KeySchema = [new KeySchemaElement { AttributeName = "Id", KeyType = KeyType.HASH }],
            BillingMode = BillingMode.PAY_PER_REQUEST
        });
    }

    public Task<ListTablesResponse> ListTables()
    {
        return ddb.ListTablesAsync(new ListTablesRequest());
    }

    public Task<PutItemResponse> PutItem()
    {
        return ddb.PutItemAsync(new PutItemRequest
        {
            TableName = "test_table", Item = new Dictionary<string, AttributeValue>
            {
                { "Id", new AttributeValue("my-id") }
            }
        });
    }
    
    public Task<DescribeTableResponse> DescribeTable()
    {
        return ddb.DescribeTableAsync(new DescribeTableRequest { TableName = "test-table" });
    }

    public Task<DeleteTableResponse> DeleteTable()
    {
        return ddb.DeleteTableAsync(new DeleteTableRequest { TableName = "test_table" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultDdb.CreateTableAsync(new CreateTableRequest
        {
            TableName = "test_table", AttributeDefinitions = [new AttributeDefinition { AttributeName = "Id", AttributeType = ScalarAttributeType.S }],
            KeySchema = [new KeySchemaElement { AttributeName = "Id", KeyType = KeyType.HASH }],
            BillingMode = BillingMode.PAY_PER_REQUEST
        }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorDdb.DeleteTableAsync(new DeleteTableRequest { TableName = "test_table_error" });
    }
}

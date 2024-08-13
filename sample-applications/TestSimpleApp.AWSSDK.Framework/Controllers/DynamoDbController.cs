using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Unity;

namespace TestSimpleApp.AWSSDK.Framework.Controllers
{
    [RoutePrefix("ddb")]
    public class DynamoDbController : ContractTestController
    {
        private readonly IAmazonDynamoDB ddb;
        private readonly IAmazonDynamoDB faultDdb;
        private readonly IAmazonDynamoDB errorDdb;

        public DynamoDbController(
            [Dependency("ddb")] IAmazonDynamoDB ddb,
            [Dependency("fault-ddb")] IAmazonDynamoDB faultDdb,
            [Dependency("error-ddb")] IAmazonDynamoDB errorDdb)
        {
            this.ddb = ddb;
            this.faultDdb = faultDdb;
            this.errorDdb = errorDdb;
        }

        [HttpGet]
        [Route("createtable/some-table")]
        public async Task<IHttpActionResult> CrateTable()
        {
            var response = await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = "test_table",
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition
                    {
                        AttributeName = "Id", AttributeType = ScalarAttributeType.S
                    }
                },
                KeySchema = new List<KeySchemaElement> { new KeySchemaElement { AttributeName = "Id", KeyType = KeyType.HASH } },
                BillingMode = BillingMode.PAY_PER_REQUEST
            });

            return Ok(response);
        }

        [HttpGet]
        [Route("put-item/some-item")]
        public async Task<IHttpActionResult> PutItem()
        {
            var response = await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = "test_table", Item = new Dictionary<string, AttributeValue>
                {
                    { "Id", new AttributeValue("my-id") }
                }
            });

            return Ok(response);
        }

        [HttpGet]
        [Route("ddb/deletetable/delete-table")]
        public async Task<IHttpActionResult> DeleteTable()
        {
            var response = await ddb.DeleteTableAsync(new DeleteTableRequest { TableName = "test_table" });
            return Ok(response);
        }

        [HttpGet]
        [Route("error")]
        public override Task<IHttpActionResult> Error()
        {
            return base.Error();
        }

        [HttpGet]
        [Route("fault")]
        public override Task<IHttpActionResult> Fault()
        {
            return base.Fault();
        }

        protected override Task CreateFault(CancellationToken cancellationToken)
        {
            return faultDdb.CreateTableAsync(new CreateTableRequest
            {
                TableName = "test_table",
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition
                    {
                        AttributeName = "Id", AttributeType = ScalarAttributeType.S
                    }
                },
                KeySchema = new List<KeySchemaElement> { new KeySchemaElement { AttributeName = "Id", KeyType = KeyType.HASH } },
                BillingMode = BillingMode.PAY_PER_REQUEST
            }, cancellationToken);
        }

        protected override Task CreateError(CancellationToken cancellationToken)
        {
            return errorDdb.CreateTableAsync(new CreateTableRequest
            {
                TableName = "test_table",
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition
                    {
                        AttributeName = "Id", AttributeType = ScalarAttributeType.S
                    }
                },
                KeySchema = new List<KeySchemaElement> { new KeySchemaElement { AttributeName = "Id", KeyType = KeyType.HASH } },
                BillingMode = BillingMode.PAY_PER_REQUEST
            }, cancellationToken);
        }
    }
}
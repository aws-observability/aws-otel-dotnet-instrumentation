using System.Web.Http;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Kinesis;
using Amazon.S3;
using Amazon.SQS;
using Owin;
using Swashbuckle.Application;
using Unity;
using Unity.AspNet.WebApi;
using Unity.Lifetime;

namespace TestSimpleApp.AWSSDK.Framework
{
    public class Startup
    {
        private readonly IUnityContainer _container = new UnityContainer();

        public Startup()
        {
            _container.RegisterSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();

            _container.RegisterInstance<IAmazonS3>("s3", new AmazonS3Client(), new SingletonLifetimeManager())
                .RegisterInstance<IAmazonS3>("fault-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>()), new SingletonLifetimeManager())
                .RegisterInstance<IAmazonS3>("error-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>()), new SingletonLifetimeManager());

            _container.RegisterInstance<IAmazonDynamoDB>("ddb", new AmazonDynamoDBClient(), new SingletonLifetimeManager())
                .RegisterInstance<IAmazonDynamoDB>("fault-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>()), new
                    SingletonLifetimeManager())
                .RegisterInstance<IAmazonDynamoDB>("error-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>()), new SingletonLifetimeManager());

            _container.RegisterInstance<IAmazonSQS>("sqs", new AmazonSQSClient(), new SingletonLifetimeManager())
                .RegisterInstance<IAmazonSQS>("fault-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>()), new
                    SingletonLifetimeManager())
                .RegisterInstance<IAmazonSQS>("error-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>()), new SingletonLifetimeManager());

            _container.RegisterInstance<IAmazonKinesis>("kinesis", new AmazonKinesisClient(), new SingletonLifetimeManager())
                .RegisterInstance<IAmazonKinesis>("fault-kinesis", new AmazonKinesisClient(AmazonClientConfigHelper.CreateConfig<AmazonKinesisConfig>()), new
                    SingletonLifetimeManager())
                .RegisterInstance<IAmazonKinesis>("error-kinesis", new AmazonKinesisClient(AmazonClientConfigHelper.CreateConfig<AmazonKinesisConfig>()), new SingletonLifetimeManager());
        }

        public void Configuration(IAppBuilder appBuilder)
        {
            var config = new HttpConfiguration();

            config.MapHttpAttributeRoutes();
            config.DependencyResolver = new UnityDependencyResolver(_container);

            config.EnableSwagger(c => c.SingleApiVersion("v1", "Todo API"))
                .EnableSwaggerUi();

            config.EnsureInitialized();

            appBuilder.UseWebApi(config);
        }
    }
}
// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace TestApplication.AWS;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string tableName = "SampleData";
        var clientConfig = new AmazonDynamoDBConfig { ServiceURL = "http://localhost:" + GetAWSServicePort(args) };
        var amazonDynamoDb = new AmazonDynamoDBClient(clientConfig);
        var ddbOperation = new DDBOperation(amazonDynamoDb, tableName);
        await ddbOperation.CreateTable();
        string id = await ddbOperation.InsertRow();
        await ddbOperation.SelectRow(id);

        // For SQS
        string queueName = "SampleQueue";
        var sqs_clientConfig = new AmazonSQSConfig { ServiceURL = "http://localhost:" + GetAWSServicePort(args) };
        var sqs = new AmazonSQSClient(sqs_clientConfig);
        var sqsOperation = new SQSOperation(sqs, queueName);
        // Create
        string queueUrl = await sqsOperation.CreateQueue();
        // Read
        await sqsOperation.ListQueues();
        await sqsOperation.GetQueueArn(queueUrl);
        // Update
        await sqsOperation.UpdateQueue(queueUrl);
        // Delete
        await sqsOperation.DeleteQueue(queueUrl);

        // For SNS
        string topicName = "SampleTopic";
        var sns_clientConfig = new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localhost:" + GetAWSServicePort(args) };
        var sns = new AmazonSimpleNotificationServiceClient(sns_clientConfig);
        var snsOperation = new SNSOperation(sns, topicName);
        // Create
        string topicArn = await snsOperation.CreateTopic();
        // Read
        await snsOperation.ListTopics();
        // Update
        await snsOperation.SendMessage(topicArn);
        // Delete
        await snsOperation.DeleteTopic(topicArn);

        // For S3
        string bucketName = "samplebucket";
        var s3_clientConfig = new AmazonS3Config
        {
            ServiceURL = "http://localhost:" + GetAWSServicePort(args),
            ForcePathStyle = true
        };
        var s3 = new AmazonS3Client(s3_clientConfig);
        var s3Operation = new S3Operation(s3, bucketName);
        // Create
        await s3Operation.CreateBucket();
        // Read
        await s3Operation.GetBucketVersioning();
        // Update
        await s3Operation.PutObject();
        // Delete
        await s3Operation.DeleteObject();
        await s3Operation.DeleteBucket();
    }

    private static string GetAWSServicePort(string[] args)
    {
        if (args.Length == 1)
        {
            return args[0];
        }

        return "4566";
    }
}

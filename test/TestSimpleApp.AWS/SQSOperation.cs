// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace TestApplication.AWS;

public class SQSOperation
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueName;

    public SQSOperation(IAmazonSQS sqs, string queueName)
    {
        _sqs = sqs;
        _queueName = queueName;
    }

    public async Task<string> CreateQueue()
    {
        var createRequest = new CreateQueueRequest(_queueName);
        CreateQueueResponse responseCreate = await _sqs.CreateQueueAsync(createRequest);
        return responseCreate.QueueUrl;
    }

    public async Task<List<string>> ListQueues()
    {
        ListQueuesResponse responseList = await _sqs.ListQueuesAsync(string.Empty);
        return responseList.QueueUrls;
    }

    public async Task UpdateQueue(string queueUrl)
    {
        await _sqs.SetQueueAttributesAsync(
            queueUrl,
            new Dictionary<string, string> { { "MessageRetentionPeriod", "600" } });
    }

    public async Task DeleteQueue(string queueUrl)
    {
        await _sqs.DeleteQueueAsync(queueUrl);
    }

    public async Task<string> GetQueueArn(string queueUrl)
    {
        GetQueueAttributesResponse responseGetAtt =
            await _sqs.GetQueueAttributesAsync(
                queueUrl,
                new List<string> { QueueAttributeName.QueueArn });
        return responseGetAtt.QueueARN;
    }
}

// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace TestApplication.AWS;

public class SNSOperation
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _topicName;

    public SNSOperation(IAmazonSimpleNotificationService sns, string topicName)
    {
        _sns = sns;
        _topicName = topicName;
    }

    public async Task<string> CreateTopic()
    {
        var createRequest = new CreateTopicRequest(_topicName);
        CreateTopicResponse responseCreate = await _sns.CreateTopicAsync(createRequest);
        return responseCreate.TopicArn;
    }

    public async Task<List<Topic>> ListTopics()
    {
        ListTopicsResponse responseList = await _sns.ListTopicsAsync();
        return responseList.Topics;
    }

    public async Task SendMessage(string topicArn)
    {
        var publishRequest = new PublishRequest
        {
            Message = "message",
            TopicArn = topicArn
        };
        await _sns.PublishAsync(publishRequest);
    }

    public async Task DeleteTopic(string topicArn)
    {
        await _sns.DeleteTopicAsync(topicArn);
    }
}

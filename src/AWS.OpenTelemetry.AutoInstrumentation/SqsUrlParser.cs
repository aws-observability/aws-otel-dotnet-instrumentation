// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Parser class for SQS URLs
/// </summary>
public class SqsUrlParser
{
    private static readonly char ArnDelimeter = ':';
    private static readonly string HttpSchema = "http://";
    private static readonly string HttpsSchema = "https://";

    /// <summary>
    /// Function that gets the SQS remote target from SQS Url
    /// </summary>
    /// <param name="sqsUrl"><see cref="string"/>Url to get the remote target from</param>
    /// <returns>parsed remote target</returns>
    public static string? GetSqsRemoteTarget(string? sqsUrl)
    {
        if (sqsUrl == null)
        {
            return null;
        }

        sqsUrl = StripSchemaFromUrl(sqsUrl);

        if (!IsSqsUrl(sqsUrl) && !IsLegacySqsUrl(sqsUrl) && !IsCustomUrl(sqsUrl))
        {
            return null;
        }

        string? region = GetRegion(sqsUrl);
        string? accountId = GetAccountId(sqsUrl);
        string? partition = GetPartition(sqsUrl);
        string? queueName = GetQueueName(sqsUrl);

        StringBuilder remoteTarget = new StringBuilder();

        if (region == null && accountId == null && partition == null && queueName == null)
        {
            return null;
        }

        if (region != null && accountId != null && partition != null && queueName != null)
        {
            remoteTarget.Append("arn");
        }

        remoteTarget
            .Append(ArnDelimeter)
            .Append(NullToEmpty(partition))
            .Append(ArnDelimeter)
            .Append("sqs")
            .Append(ArnDelimeter)
            .Append(NullToEmpty(region))
            .Append(ArnDelimeter)
            .Append(NullToEmpty(accountId))
            .Append(ArnDelimeter)
            .Append(queueName);

        return remoteTarget.ToString();
    }

    private static string StripSchemaFromUrl(string url)
    {
        return url.Replace(HttpSchema, string.Empty).Replace(HttpsSchema, string.Empty);
    }

    private static string? GetRegion(string sqsUrl)
    {
        if (sqsUrl == null)
        {
            return null;
        }

        if (sqsUrl.StartsWith("queue.amazonaws.com/"))
        {
            return "us-east-1";
        }
        else if (IsSqsUrl(sqsUrl))
        {
            return GetRegionFromSqsUrl(sqsUrl);
        }
        else if (IsLegacySqsUrl(sqsUrl))
        {
            return GetRegionFromLegacySqsUrl(sqsUrl);
        }
        else
        {
            return null;
        }
    }

    private static bool IsSqsUrl(string sqsUrl)
    {
        string[] split = sqsUrl.Split("/");

        return split.Length == 3
            && split[0].StartsWith("sqs.")
            && split[0].EndsWith(".amazonaws.com")
            && IsAccountId(split[1])
            && IsValidQueueName(split[2]);
    }

    private static bool IsLegacySqsUrl(string sqsUrl)
    {
        string[] split = sqsUrl.Split("/");

        return split.Length == 3
            && split[0].EndsWith(".queue.amazonaws.com")
            && IsAccountId(split[1])
            && IsValidQueueName(split[2]);
    }

    private static bool IsCustomUrl(string sqsUrl)
    {
        string[] split = sqsUrl.Split("/");
        return split.Length == 3 && IsAccountId(split[1]) && IsValidQueueName(split[2]);
    }

    private static bool IsValidQueueName(string input)
    {
        if (input.Length == 0 || input.Length > 80)
        {
            return false;
        }

        foreach (char c in input.ToCharArray())
        {
            if (c != '_' && c != '-' && !char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccountId(string input)
    {
        if (input.Length != 12)
        {
            return false;
        }

        try
        {
            long.Parse(input);
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    private static string? GetRegionFromSqsUrl(string sqsUrl)
    {
        string[] split = sqsUrl.Split("\\.");

        if (split.Length >= 2)
        {
            return split[1];
        }

        return null;
    }

    private static string GetRegionFromLegacySqsUrl(string sqsUrl)
    {
        string[] split = sqsUrl.Split("\\.");
        return split[0];
    }

    private static string? GetAccountId(string sqsUrl)
    {
        if (sqsUrl == null)
        {
            return null;
        }

        string[] split = sqsUrl.Split("/");
        if (split.Length >= 2)
        {
            return split[1];
        }

        return null;
    }

    private static string? GetPartition(string sqsUrl)
    {
        string? region = GetRegion(sqsUrl);

        if (region == null)
        {
            return null;
        }

        if (region.StartsWith("us-gov-"))
        {
            return "aws-us-gov";
        }
        else if (region.StartsWith("cn-"))
        {
            return "aws-cn";
        }
        else
        {
            return "aws";
        }
    }

    private static string? GetQueueName(string sqsUrl)
    {
        string[] split = sqsUrl.Split("/");

        if (split.Length >= 3)
        {
            return split[2];
        }

        return null;
    }

    private static string NullToEmpty(string? input)
    {
        return input == null ? string.Empty : input;
    }
}

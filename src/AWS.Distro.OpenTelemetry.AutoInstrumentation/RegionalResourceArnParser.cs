// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

public class RegionalResourceArnParser
{
    public static string? GetAccountId(string? arn)
    {
        if (IsArn(arn))
        {
            return arn!.Split(':')[4];
        }

        return null;
    }

    public static string? GetRegion(string? arn)
    {
        if (IsArn(arn))
        {
            return arn!.Split(':')[3];
        }

        return null;
    }

    public static string? ExtractKinesisStreamNameFromArn(string? arn)
    {
        return ExtractResourceNameFromArn(arn)?.Replace("stream/", string.Empty);
    }

    public static string? ExtractDynamoDbTableNameFromArn(string? arn)
    {
        return ExtractResourceNameFromArn(arn)?.Replace("table/", string.Empty);
    }

    public static string? ExtractResourceNameFromArn(string? arn)
    {
        if (IsArn(arn))
        {
            return arn!.Split(':').Last();
        }

        return null;
    }

    public static bool IsArn(string? arn)
    {
        // Check if arn follow the format:
        // arn:partition:service:region:account-id:resource-type/resource-id or
        // arn:partition:service:region:account-id:resource-type:resource-id
        if (arn == null || !arn.StartsWith("arn", StringComparison.Ordinal))
        {
            return false;
        }

        string[] arnParts = arn.Split(':');
        return arnParts.Length >= 6 && IsAccountId(arnParts[4]);
    }

    private static bool IsAccountId(string input)
    {
        if (input == null)
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
}

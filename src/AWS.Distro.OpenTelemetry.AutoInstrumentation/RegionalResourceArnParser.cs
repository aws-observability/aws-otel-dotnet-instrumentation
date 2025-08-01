// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

public class RegionalResourceArnParser
{
    public static string? GetAccountId(string? arn) => ParseArn(arn)?[4];

    public static string? GetRegion(string? arn) => ParseArn(arn)?[3];

    public static string? ExtractKinesisStreamNameFromArn(string? arn) =>
        ExtractResourceNameFromArn(arn)?.Replace("stream/", string.Empty);

    public static string? ExtractDynamoDbTableNameFromArn(string? arn) =>
        ExtractResourceNameFromArn(arn)?.Replace("table/", string.Empty);

    public static string? ExtractResourceNameFromArn(string? arn) =>
        ParseArn(arn) is var parts && parts != null ? parts[parts.Length - 1] : null;

    /// <summary>
    /// Parses ARN with formats:
    /// arn:partition:service:region:account-id:resource-type/resource-id or
    /// arn:partition:service:region:account-id:resource-type:resource-id
    /// </summary>
    private static string[] ? ParseArn(string? arn)
    {
        if (arn == null || !arn.StartsWith("arn:"))
        {
            return null;
        }

        var parts = arn.Split(':');
        return parts.Length >= 6 && IsAccountId(parts[4]) ? parts : null;
    }

    private static bool IsAccountId(string input)
    {
        return long.TryParse(input, out _);
    }
}

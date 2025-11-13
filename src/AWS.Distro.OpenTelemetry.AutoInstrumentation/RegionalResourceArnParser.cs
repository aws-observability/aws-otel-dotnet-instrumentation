// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Parser for AWS regional resource ARNs.
/// </summary>
public class RegionalResourceArnParser
{
    /// <summary>
    /// Gets the account ID from an AWS ARN.
    /// </summary>
    /// <param name="arn">The ARN to parse.</param>
    /// <returns>The account ID or null if not found.</returns>
    public static string? GetAccountId(string? arn) => ParseArn(arn)?[4];

    /// <summary>
    /// Gets the region from an AWS ARN.
    /// </summary>
    /// <param name="arn">The ARN to parse.</param>
    /// <returns>The region or null if not found.</returns>
    public static string? GetRegion(string? arn) => ParseArn(arn)?[3];

    /// <summary>
    /// Extracts the Kinesis stream name from an ARN.
    /// </summary>
    /// <param name="arn">The Kinesis stream ARN.</param>
    /// <returns>The stream name or null if not found.</returns>
    public static string? ExtractKinesisStreamNameFromArn(string? arn) =>
        ExtractResourceNameFromArn(arn)?.Replace("stream/", string.Empty);

    /// <summary>
    /// Extracts the DynamoDB table name from an ARN.
    /// </summary>
    /// <param name="arn">The DynamoDB table ARN.</param>
    /// <returns>The table name or null if not found.</returns>
    public static string? ExtractDynamoDbTableNameFromArn(string? arn) =>
        ExtractResourceNameFromArn(arn)?.Replace("table/", string.Empty);

    /// <summary>
    /// Extracts the resource name from an AWS ARN.
    /// </summary>
    /// <param name="arn">The ARN to parse.</param>
    /// <returns>The resource name or null if not found.</returns>
    public static string? ExtractResourceNameFromArn(string? arn) =>
        ParseArn(arn) is var parts && parts != null ? parts[parts.Length - 1] : null;

    /// <summary>
    /// Parses ARN with formats:
    /// arn:partition:service:region:account-id:resource-type/resource-id or
    /// arn:partition:service:region:account-id:resource-type:resource-id
    /// </summary>
    private static string[]? ParseArn(string? arn)
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

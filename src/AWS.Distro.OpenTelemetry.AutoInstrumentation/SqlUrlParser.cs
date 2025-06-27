// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Parser class for SQS URLs
/// </summary>
public class SqsUrlParser
{
    private static readonly string HttpSchema = "http://";
    private static readonly string HttpsSchema = "https://";

    /// <summary>
    /// Best-effort logic to extract queue name from an HTTP url. This method should only be used with
    /// a string that is, with reasonably high confidence, an SQS queue URL. Handles new/legacy/some
    /// custom URLs. Essentially, we require that the URL should have exactly three parts, delimited by
    /// /'s (excluding schema), the second part should be an account id consisting of digits, and the third part
    /// should be a valid queue name, per SQS naming conventions.
    /// </summary>
    /// <param name="url"><see cref="string"/>Url to get the remote target from</param>
    /// <returns>parsed remote target</returns>
    public static string? GetQueueName(string? url)
    {
        if (url == null)
        {
            return null;
        }

        string urlWithoutProtocol = url.Replace(HttpSchema, string.Empty).Replace(HttpsSchema, string.Empty);
        string[] splitUrl = urlWithoutProtocol.Split('/');
        if (splitUrl.Length == 3 && IsAccountId(splitUrl[1]) && IsValidQueueName(splitUrl[2]))
        {
            return splitUrl[2];
        }

        return null;
    }

    public static string? GetAccountId(string? url) => ParseUrl(url).accountId;

    public static string? GetRegion(string? url) => ParseUrl(url).region;

    public static (string? QueueName, string? accountId, string? region) ParseUrl(string? url)
    {
        if (url == null)
        {
            return (null, null, null);
        }

        string urlWithoutProtocol = url.Replace(HttpSchema, string.Empty).Replace(HttpsSchema, string.Empty);
        string[] splitUrl = urlWithoutProtocol.Split('/');

        if (
            splitUrl.Length != 3 ||
            !splitUrl[0].StartsWith("sqs", StringComparison.OrdinalIgnoreCase) ||
            !IsAccountId(splitUrl[1]) ||
            !IsValidQueueName(splitUrl[2]))
        {
            return (null, null, null);
        }

        string domain = splitUrl[0];
        string[] domainParts = domain.Split('.');

        return (
            splitUrl[2],
            splitUrl[1],
            domainParts.Length == 4 ? domainParts[1] : null);
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

    private static bool IsValidQueueName(string input)
    {
        if (input == null || input.Length == 0 || input.Length > 80)
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
}

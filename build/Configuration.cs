// Copyright Splunk Inc
// SPDX-License-Identifier: Apache-2.0
// Modifications Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

using System.ComponentModel;
using Nuke.Common.Tooling;

[TypeConverter(typeof(TypeConverter<Configuration>))]
public class Configuration : Enumeration
{
    public static Configuration Debug = new Configuration { Value = nameof(Debug) };
    public static Configuration Release = new Configuration { Value = nameof(Release) };

    public static implicit operator string(Configuration configuration)
    {
        return configuration.Value;
    }
}

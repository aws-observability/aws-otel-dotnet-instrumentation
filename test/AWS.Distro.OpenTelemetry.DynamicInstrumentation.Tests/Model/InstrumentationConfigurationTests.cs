// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Model;

public class InstrumentationConfigurationTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_FullProbeConfig_ReturnsCorrectValues()
    {
        var json = """
        {
            "InstrumentationType": "PROBE",
            "LocationHash": "aabb000000000001",
            "Location": {
                "CodeLocation": {
                    "Language": "Dotnet",
                    "CodeUnit": "MyApp.Services",
                    "ClassName": "OrderService",
                    "MethodName": "ProcessOrder",
                    "FilePath": "OrderService.cs",
                    "LineNumber": 0
                }
            },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureReturn": true,
                    "CaptureStackTrace": true,
                    "CaptureArguments": [],
                    "CaptureLocals": [],
                    "CaptureLimits": {
                        "MaxStringLength": 200,
                        "MaxCollectionWidth": 15,
                        "MaxHits": 50
                    }
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().NotBeNull();
        config!.Type.Should().Be(InstrumentationType.PROBE);
        config.CodeUnit.Should().Be("MyApp.Services");
        config.ClassName.Should().Be("OrderService");
        config.MethodName.Should().Be("ProcessOrder");
        config.FilePath.Should().Be("OrderService.cs");
        config.LineNumber.Should().Be(0);
        config.LocationHash.Should().Be("aabb000000000001");
        config.IsMethodLevel.Should().BeTrue();
        config.IsLineLevel.Should().BeFalse();
        config.MethodKey.Should().Be("MyApp.Services.OrderService.ProcessOrder");
        config.InstrumentationKey.Should().Be("MyApp.Services.OrderService.ProcessOrder");
    }

    [Fact]
    public void Parse_BreakpointWithLineNumber_IsLineLevel()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "aabb000000000002",
            "Location": {
                "CodeLocation": {
                    "Language": "Dotnet",
                    "CodeUnit": "MyApp",
                    "ClassName": "Calculator",
                    "MethodName": "Add",
                    "LineNumber": 42
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().NotBeNull();
        config!.Type.Should().Be(InstrumentationType.BREAKPOINT);
        config.LineNumber.Should().Be(42);
        config.IsLineLevel.Should().BeTrue();
        config.IsMethodLevel.Should().BeFalse();
        config.InstrumentationKey.Should().Be("MyApp.Calculator.Add:42");
    }

    [Fact]
    public void Parse_ProbeWithLineNumber_ForcesLineNumberToZero()
    {
        var json = """
        {
            "InstrumentationType": "PROBE",
            "LocationHash": "hash1",
            "Location": {
                "CodeLocation": {
                    "Language": "Dotnet",
                    "CodeUnit": "MyApp",
                    "ClassName": "Svc",
                    "MethodName": "Run",
                    "LineNumber": 99
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().NotBeNull();
        config!.LineNumber.Should().Be(0);
        config.IsMethodLevel.Should().BeTrue();
    }

    [Fact]
    public void Parse_NonDotnetLanguage_ReturnsNull()
    {
        var json = """
        {
            "InstrumentationType": "PROBE",
            "LocationHash": "hash1",
            "Location": {
                "CodeLocation": {
                    "Language": "Java",
                    "CodeUnit": "com.example",
                    "ClassName": "Svc",
                    "MethodName": "Run"
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingLanguage_StillParses()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "Location": {
                "CodeLocation": {
                    "CodeUnit": "MyApp",
                    "ClassName": "Svc",
                    "MethodName": "Run"
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().NotBeNull();
        config!.ClassName.Should().Be("Svc");
    }

    [Fact]
    public void Parse_CaptureArgumentsNull_MeansSkipCapture()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureReturn": false
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().NotBeNull();
        config!.Capture.CaptureArguments.Should().BeNull();
        config.Capture.CaptureLocals.Should().BeNull();
        config.Capture.CaptureReturn.Should().BeFalse();
    }

    [Fact]
    public void Parse_CaptureArgumentsEmpty_MeansCaptureAll()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureArguments": [],
                    "CaptureLocals": []
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.Capture.CaptureArguments.Should().BeEmpty();
        config.Capture.CaptureLocals.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CaptureArgumentsWithNames_MeansFilteredCapture()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureArguments": ["orderId", "quantity"]
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.Capture.CaptureArguments.Should().Equal("orderId", "quantity");
    }

    [Fact]
    public void Parse_CaptureLimits_ClampedToValidRanges()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureLimits": {
                        "MaxStringLength": 9999,
                        "MaxCollectionWidth": 9999,
                        "MaxObjectDepth": 9999,
                        "MaxHits": 5000
                    }
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.Capture.MaxStringLength.Should().Be(255);
        config.Capture.MaxCollectionWidth.Should().Be(20);
        config.Capture.MaxObjectDepth.Should().Be(5);
        config.Capture.MaxHits.Should().Be(1000);
    }

    [Fact]
    public void Parse_ProbeMaxHits_IsUnlimited()
    {
        var json = """
        {
            "InstrumentationType": "PROBE",
            "LocationHash": "hash1",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } },
            "CaptureConfiguration": {
                "CodeCapture": {
                    "CaptureLimits": { "MaxHits": 50 }
                }
            }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.Capture.MaxHits.Should().BeNull();
    }

    [Fact]
    public void Parse_ExpiresAt_UnixSeconds()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "ExpiresAt": 1735689600,
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.ExpiresAt.Should().NotBeNull();
        config.ExpiresAt!.Value.Year.Should().Be(2025);
    }

    [Fact]
    public void Parse_ExpiresAt_Iso8601()
    {
        var json = """
        {
            "InstrumentationType": "BREAKPOINT",
            "LocationHash": "hash1",
            "ExpiresAt": "2025-12-31T23:59:59Z",
            "Location": { "CodeLocation": { "CodeUnit": "A", "ClassName": "B", "MethodName": "C" } }
        }
        """;

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config!.ExpiresAt.Should().NotBeNull();
        config.ExpiresAt!.Value.Year.Should().Be(2025);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        var json = """{ "garbage": true }""";

        var config = InstrumentationConfiguration.Parse(Parse(json));

        config.Should().BeNull();
    }
}

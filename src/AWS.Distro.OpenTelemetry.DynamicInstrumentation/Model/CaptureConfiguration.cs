// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

public sealed record CaptureConfiguration(
    string[]? CaptureArguments,
    string[]? CaptureLocals,
    bool CaptureReturn,
    bool CaptureStackTrace,
    int MaxStringLength,
    int MaxCollectionWidth,
    int MaxCollectionDepth,
    int MaxObjectDepth,
    int MaxFieldsPerObject,
    int MaxStackFrames,
    int? MaxHits)
{
    private const int DefaultMaxStringLength = 255;
    private const int DefaultMaxCollectionWidth = 20;
    private const int DefaultMaxCollectionDepth = 3;
    private const int DefaultMaxObjectDepth = 3;
    private const int DefaultMaxFieldsPerObject = 20;
    private const int DefaultMaxStackFrames = 20;
    private const int DefaultMaxHits = 100;

    public bool HasHitLimit => MaxHits.HasValue;

    public static CaptureConfiguration Default => new(
        CaptureArguments: Array.Empty<string>(),
        CaptureLocals: Array.Empty<string>(),
        CaptureReturn: true,
        CaptureStackTrace: true,
        MaxStringLength: DefaultMaxStringLength,
        MaxCollectionWidth: DefaultMaxCollectionWidth,
        MaxCollectionDepth: DefaultMaxCollectionDepth,
        MaxObjectDepth: DefaultMaxObjectDepth,
        MaxFieldsPerObject: DefaultMaxFieldsPerObject,
        MaxStackFrames: DefaultMaxStackFrames,
        MaxHits: DefaultMaxHits);

    public static int ClampMaxStringLength(int value) => Math.Clamp(value, 1, 255);
    public static int ClampMaxCollectionWidth(int value) => Math.Clamp(value, 1, 20);
    public static int ClampMaxCollectionDepth(int value) => Math.Clamp(value, 1, 5);
    public static int ClampMaxObjectDepth(int value) => Math.Clamp(value, 1, 5);
    public static int ClampMaxFieldsPerObject(int value) => Math.Clamp(value, 1, 20);
    public static int ClampMaxStackFrames(int value) => Math.Clamp(value, 1, 20);
    public static int ClampMaxHits(int value) => Math.Clamp(value, 1, 1000);
}

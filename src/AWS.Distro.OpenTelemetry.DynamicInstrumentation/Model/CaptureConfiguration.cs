// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

/// <summary>
/// What to capture at an instrumentation point and the resource limits applied while capturing.
/// </summary>
/// <param name="CaptureArguments">Argument names to capture; empty captures all, null captures none.</param>
/// <param name="CaptureLocals">Local variable names to capture (line-level).</param>
/// <param name="CaptureReturn">Whether to capture the method's return value.</param>
/// <param name="CaptureStackTrace">Whether to capture a stack trace at the hit.</param>
/// <param name="MaxStringLength">Maximum length of a captured string before truncation.</param>
/// <param name="MaxCollectionWidth">Maximum number of elements captured per collection.</param>
/// <param name="MaxCollectionDepth">Maximum nesting depth for collections and dictionaries.</param>
/// <param name="MaxObjectDepth">Maximum nesting depth for object graphs.</param>
/// <param name="MaxFieldsPerObject">Maximum number of fields captured per object.</param>
/// <param name="MaxStackFrames">Maximum number of stack frames captured.</param>
/// <param name="MaxHits">Maximum number of times the instrumentation fires; null means unlimited.</param>
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

    /// <summary>Gets a value indicating whether a finite hit limit is set.</summary>
    public bool HasHitLimit => this.MaxHits.HasValue;

    /// <summary>Gets the default capture configuration used when none is supplied.</summary>
    public static CaptureConfiguration Default => new(
        CaptureArguments: [],
        CaptureLocals: [],
        CaptureReturn: true,
        CaptureStackTrace: true,
        MaxStringLength: DefaultMaxStringLength,
        MaxCollectionWidth: DefaultMaxCollectionWidth,
        MaxCollectionDepth: DefaultMaxCollectionDepth,
        MaxObjectDepth: DefaultMaxObjectDepth,
        MaxFieldsPerObject: DefaultMaxFieldsPerObject,
        MaxStackFrames: DefaultMaxStackFrames,
        MaxHits: DefaultMaxHits);

    /// <summary>Clamps a MaxStringLength value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxStringLength(int value) => Math.Clamp(value, 1, 255);

    /// <summary>Clamps a MaxCollectionWidth value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxCollectionWidth(int value) => Math.Clamp(value, 1, 20);

    /// <summary>Clamps a MaxCollectionDepth value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxCollectionDepth(int value) => Math.Clamp(value, 1, 5);

    /// <summary>Clamps a MaxObjectDepth value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxObjectDepth(int value) => Math.Clamp(value, 1, 5);

    /// <summary>Clamps a MaxFieldsPerObject value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxFieldsPerObject(int value) => Math.Clamp(value, 1, 20);

    /// <summary>Clamps a MaxStackFrames value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxStackFrames(int value) => Math.Clamp(value, 1, 20);

    /// <summary>Clamps a MaxHits value to the supported range.</summary>
    /// <param name="value">The requested value.</param>
    /// <returns>The clamped value.</returns>
    public static int ClampMaxHits(int value) => Math.Clamp(value, 1, 1000);
}

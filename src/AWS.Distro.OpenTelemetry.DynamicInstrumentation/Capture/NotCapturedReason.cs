// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>
/// Why a value was not fully captured. Emitted alongside a truncated/partial value so consumers can
/// distinguish limit reasons. Names mirror the Java/Python/JS SDKs (DEPTH/FIELD_COUNT/COLLECTION_SIZE/
/// TIMEOUT/ALREADY_CAPTURED).
/// </summary>
internal enum NotCapturedReason
{
    /// <summary>The value was fully captured; nothing was dropped.</summary>
    None,

    /// <summary>Object- or collection-nesting depth limit was reached.</summary>
    Depth,

    /// <summary>The per-object field count limit was reached.</summary>
    FieldCount,

    /// <summary>The per-collection element (width) limit was reached.</summary>
    CollectionSize,

    /// <summary>The serialization time budget was exceeded.</summary>
    Timeout,

    /// <summary>The value was already captured earlier in this graph (reference cycle).</summary>
    AlreadyCaptured,
}

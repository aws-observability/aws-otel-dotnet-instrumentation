// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

internal sealed record StackFrameInfo(string? FileName, string? MethodName, int LineNumber);

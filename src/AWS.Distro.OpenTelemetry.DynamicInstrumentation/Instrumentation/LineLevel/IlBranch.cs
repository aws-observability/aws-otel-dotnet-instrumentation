// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// One decoded jump: the offset of the branch instruction, and the offset it lands on.
/// </summary>
/// <param name="Source">Offset of the branch (or switch) instruction itself.</param>
/// <param name="Target">Offset the jump lands on.</param>
// The SOURCE is the part that matters and the part a target-only set cannot express: a jump from before a
// statement to after it means some path skips that statement, while a jump inside the statement's own span
// (a ternary, `&&`, `??`) rejoins with the statement having executed either way.
internal readonly record struct IlBranch(uint Source, uint Target);

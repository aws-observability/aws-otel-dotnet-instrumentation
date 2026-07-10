// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// This file is used by Code Analysis to maintain SuppressMessage
// attributes applied to this project.
using System.Diagnostics.CodeAnalysis;

// The HTTP request/response API model records are intentionally grouped in the client file
// alongside the client that owns them (matches the sibling project's SA1402 handling).
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "API model records grouped with their client", Scope = "type", Target = "~T:AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client.FetchConfigurationsRequest")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "API model records grouped with their client", Scope = "type", Target = "~T:AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client.FetchConfigurationsRawResponse")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "API model records grouped with their client", Scope = "type", Target = "~T:AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client.FetchConfigurationsResponse")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "API model records grouped with their client", Scope = "type", Target = "~T:AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client.StatusEntry")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "API model records grouped with their client", Scope = "type", Target = "~T:AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client.ReportStatusRequest")]

// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder.Build~AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessor")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder.Create~AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder.SetAttributesKeysToPropagate(System.Collections.Generic.List{System.String})~AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder.SetPropagationDataExtractor(System.Func{System.Diagnostics.Activity,System.String})~AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder.SetPropagationDataKey(System.String)~AWS.Distro.OpenTelemetry.AutoInstrumentation.AttributePropagatingSpanProcessorBuilder")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributesSpanExporterBuilder.Build~AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributesSpanExporter")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Reviewed", Scope = "member", Target = "~M:AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributesSpanExporterBuilder.Create(OpenTelemetry.BaseExporter{System.Diagnostics.Activity},OpenTelemetry.Resources.Resource)~AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributesSpanExporterBuilder")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Reviewed", Scope = "type", Target = "~T:ByteStringConverter")]
[assembly: SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Reviewed", Scope = "type", Target = "~T:ByteStringConverter")]
[assembly: SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Reviewed", Scope = "type", Target = "~T:OtlpUdpExporter")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Reviewed", Scope = "type", Target = "~T:SpanKindConverter")]
[assembly: SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Reviewed", Scope = "type", Target = "~T:SpanKindConverter")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Reviewed", Scope = "type", Target = "~T:UdpExporter")]
[assembly: SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Reviewed", Scope = "type", Target = "~T:UdpExporter")]
[assembly: SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1011:Closing square brackets should be spaced correctly", Justification = "Reviewed", Scope = "member", Target = "~M:OtlpUdpExporter.Export(OpenTelemetry.Batch{System.Diagnostics.Activity}@)~OpenTelemetry.ExportResult")]
[assembly: SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1011:Closing square brackets should be spaced correctly", Justification = "Reviewed", Scope = "member", Target = "~M:OtlpUdpExporter.SerializeSpans(OpenTelemetry.Batch{System.Diagnostics.Activity})~System.Byte[]")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Reviewed", Scope = "type", Target = "~T:StatusCodeConverter")]

// TODO, review these suppressions.

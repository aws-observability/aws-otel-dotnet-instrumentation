// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Utils;

/// <summary>
/// Tests for <see cref="ShutdownBudget" />, the single deadline every ServiceEvents shutdown wait
/// draws from. The collector tests cover the same property end to end but have to assert on wall
/// clock; these pin the arithmetic deterministically.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ShutdownBudgetTests
{
    [Fact]
    public void Clamp_WhenTheRequestFitsInTheBudget_ReturnsTheRequestUnchanged()
    {
        var budget = ShutdownBudget.FromNow(TimeSpan.FromSeconds(5));

        budget.Clamp(TimeSpan.FromMilliseconds(250))
            .Should().Be(
                TimeSpan.FromMilliseconds(250),
                "a wait well inside the deadline should not be shortened");
    }

    [Fact]
    public void Clamp_WhenTheRequestExceedsTheBudget_ReturnsWhatRemains()
    {
        var budget = ShutdownBudget.FromNow(TimeSpan.FromMilliseconds(200));

        var clamped = budget.Clamp(TimeSpan.FromSeconds(30));

        clamped.Should().BePositive("some budget was still left to wait on");
        clamped.Should().BeLessThanOrEqualTo(
            TimeSpan.FromMilliseconds(200),
            "a wait past the deadline must be cut down to the remaining time, which is the whole " +
            "point: one slow step must not consume the window the later steps and the exporter " +
            "flush still need");
    }

    [Fact]
    public void Clamp_OnceTheDeadlineHasPassed_ReturnsZeroRatherThanBlocking()
    {
        var budget = ShutdownBudget.FromNow(TimeSpan.Zero);

        budget.Clamp(TimeSpan.FromSeconds(30))
            .Should().Be(
                TimeSpan.Zero,
                "a caller that has run out of budget must stop waiting, not block on a negative or " +
                "wrapped-around timeout");
    }

    [Fact]
    public void Remaining_OnceTheDeadlineHasPassed_IsNeverNegative()
    {
        var budget = ShutdownBudget.FromNow(TimeSpan.Zero);

        Thread.Sleep(20);

        budget.Remaining.Should().Be(
            TimeSpan.Zero,
            "a negative remaining time would be passed straight to WaitOne as an invalid timeout");
    }

    [Fact]
    public void Remaining_DecreasesAsTheBudgetIsConsumed()
    {
        var budget = ShutdownBudget.FromNow(TimeSpan.FromSeconds(5));
        var before = budget.Remaining;

        Thread.Sleep(50);

        budget.Remaining.Should().BeLessThan(
            before,
            "the deadline is fixed at construction, so later reads must see less time left; a budget " +
            "that restarted per read would let sequential waits each take the full window");
    }

    [Fact]
    public void Default_LeavesRoomInsideTheProcessExitWindow()
    {
        // The runtime allows roughly two seconds for ProcessExit handlers. The budget covers only our
        // waits; the OTLP exporter flushes run afterwards and need what is left. A default at or past
        // the window would mean the flush never happens, which defeats the purpose of draining.
        ShutdownBudget.Default.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "the shutdown budget must leave room for the exporter flushes that follow it");
    }
}

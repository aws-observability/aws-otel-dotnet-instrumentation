// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Reflection;
using System.Text;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Client;

public class ConfigurationPollerTests
{
    [Fact]
    public void ComputeSleepMs_NormalInterval_ReturnsBaseWithJitter()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 0, degraded: false);

        // Base is 60s, jitter adds up to 25% = 60000 to 75000
        result.Should().BeInRange(60_000, 75_000);
    }

    [Fact]
    public void ComputeSleepMs_FirstFailure_Returns10Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 1, degraded: false);

        result.Should().Be(10_000);
    }

    [Fact]
    public void ComputeSleepMs_SecondFailure_Returns30Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 2, degraded: false);

        result.Should().Be(30_000);
    }

    [Fact]
    public void ComputeSleepMs_ThirdFailure_Returns120Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 3, degraded: false);

        result.Should().Be(120_000);
    }

    [Fact]
    public void ComputeSleepMs_Degraded_Returns300SecondsWithJitter()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 5, degraded: true);

        // Degraded base is 300s, jitter adds up to 25% = 300000 to 375000
        result.Should().BeInRange(300_000, 375_000);
    }

    [Fact]
    public void AddJitter_AlwaysPositive_NeverExceeds25Percent()
    {
        for (int i = 0; i < 100; i++)
        {
            var result = ConfigurationPoller.AddJitter(60_000);
            result.Should().BeInRange(60_000, 75_000);
        }
    }

    [Fact]
    public void AddJitter_ZeroBase_ReturnsZero()
    {
        var result = ConfigurationPoller.AddJitter(0);
        result.Should().Be(0);
    }

    // Regression: the old code degraded at `failedAttempts >= BackoffDelaysMs.Length` (== 3),
    // which flipped degraded ON the 3rd attempt and skipped the 120s tier entirely
    // (10s→30s→300s). Degrade must only happen AFTER all 3 tiers are used.
    [Theory]
    [InlineData(1, false)] // 10s tier
    [InlineData(2, false)] // 30s tier
    [InlineData(3, false)] // 120s tier — must NOT be degraded yet
    [InlineData(4, true)]  // all tiers exhausted → degraded
    [InlineData(5, true)]
    public void ShouldDegrade_OnlyAfterAllBackoffTiers(int failedAttempts, bool expected)
    {
        ConfigurationPoller.ShouldDegrade(failedAttempts).Should().Be(expected);
    }

    [Fact]
    public void BackoffSequence_ReachesAllThreeTiersBeforeDegrading()
    {
        // Walk the exact sequence PollLoop produces: each failure increments failedAttempts,
        // degraded is derived from ShouldDegrade, then ComputeSleepMs picks the delay.
        int[] observed = new int[4];
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var degraded = ConfigurationPoller.ShouldDegrade(attempt);
            observed[attempt - 1] = ConfigurationPoller.ComputeSleepMs(60_000, attempt, degraded);
        }

        observed[0].Should().Be(10_000);
        observed[1].Should().Be(30_000);
        observed[2].Should().Be(120_000); // the tier the off-by-one used to skip
        observed[3].Should().BeInRange(300_000, 375_000); // degraded
    }

    // --- Config-change fingerprint path ---
    // These drive the private FetchAndApply loop directly (via reflection) so the change-detection
    // fingerprint logic can be exercised deterministically without spinning up the polling threads.

    [Fact]
    public async Task FetchAndApply_IdenticalConfigsOnSecondPoll_SkipsRetransform()
    {
        // Same config returned twice with Changed=true. The fingerprint (LocationHash:CreatedAt)
        // SetEquals the previous one, so the callback must NOT fire again — no re-transform.
        var invocations = new List<List<InstrumentationConfiguration>>();
        var client = ClientReturning(
            ChangedResponse(ProbeConfig("aabb000000000001")),
            ChangedResponse(ProbeConfig("aabb000000000001")));
        var poller = CreatePoller(client, invocations.Add);

        (await InvokeFetchAndApply(poller, InstrumentationType.PROBE)).Should().BeTrue();
        (await InvokeFetchAndApply(poller, InstrumentationType.PROBE)).Should().BeTrue();

        invocations.Should().HaveCount(1, "an identical fingerprint must not re-invoke the apply callback");
        invocations[0].Should().ContainSingle().Which.LocationHash.Should().Be("aabb000000000001");
    }

    [Fact]
    public async Task FetchAndApply_RemovedKeyOnSecondPoll_TriggersRemoval()
    {
        // Two configs, then one removed. The fingerprint changes, so the callback fires again
        // with the reduced active set (the removed key is gone).
        var invocations = new List<List<InstrumentationConfiguration>>();
        var client = ClientReturning(
            ChangedResponse(ProbeConfig("hashKeep"), ProbeConfig("hashRemove")),
            ChangedResponse(ProbeConfig("hashKeep")));
        var poller = CreatePoller(client, invocations.Add);

        await InvokeFetchAndApply(poller, InstrumentationType.PROBE);
        await InvokeFetchAndApply(poller, InstrumentationType.PROBE);

        invocations.Should().HaveCount(2, "removing a key changes the fingerprint and must re-apply");
        invocations[0].Select(c => c.LocationHash).Should().BeEquivalentTo("hashKeep", "hashRemove");
        invocations[1].Select(c => c.LocationHash).Should().BeEquivalentTo(new[] { "hashKeep" });
    }

    [Fact]
    public async Task FetchAndApply_ApplyThrows_DoesNotPersistFingerprint_SoNextPollRetries()
    {
        // Fixed behavior (parity with Java/Python/JS): lastFingerprint is persisted only AFTER the
        // apply callback succeeds. When the callback throws, the fingerprint stays stale, so an
        // identical next poll does NOT SetEquals it and the apply is RETRIED.
        var invocationCount = 0;
        var client = ClientReturning(
            ChangedResponse(ProbeConfig("aabb000000000099")),
            ChangedResponse(ProbeConfig("aabb000000000099")));
        var poller = CreatePoller(client, _ =>
        {
            invocationCount++;
            throw new InvalidOperationException("apply failed");
        });

        // First poll: the callback runs and throws — the exception propagates out of FetchAndApply.
        var act = async () => await InvokeFetchAndApply(poller, InstrumentationType.PROBE);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("apply failed");

        // The fingerprint was NOT persisted because the callback threw.
        GetLastFingerprint(poller).Should().BeEmpty("fingerprint is persisted only after a successful apply");

        // Second identical poll: fingerprint is still stale, so the apply is retried.
        var act2 = async () => await InvokeFetchAndApply(poller, InstrumentationType.PROBE);
        await act2.Should().ThrowAsync<InvalidOperationException>().WithMessage("apply failed");

        invocationCount.Should().Be(2, "a thrown apply leaves the fingerprint stale, so the next poll retries");
    }

    [Fact(Skip = "parity: needs staleness-warning / forced-resync — not yet implemented (source has only a TODO at ConfigurationPoller.cs:43)")]
    public void StalenessWarning_ForcesFullResync_WhenNoSuccessWithinWindow()
    {
        // Intended: when no successful sync occurs within the staleness window (30m probes / 5m
        // breakpoints), the poller should clear SyncedAt and force a full resync (and/or warn).
        // No such logic exists in ConfigurationPoller today, so this is a tracked placeholder.
    }

    // --- Fingerprint-path helpers ---

    private static ConfigurationPoller CreatePoller(
        DynamicInstrumentationClient client, Action<List<InstrumentationConfiguration>> onChanged) =>
        new(client, probeIntervalSeconds: 60, breakpointIntervalSeconds: 60, onChanged, CancellationToken.None);

    private static DynamicInstrumentationClient ClientReturning(params string[] responseBodies)
    {
        var queue = new Queue<string>(responseBodies);
        var handler = new MockHttpHandler(_ =>
        {
            var body = queue.Count > 0 ? queue.Dequeue() : """{ "Changed": false }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
        return new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "test-service", "test-env");
    }

    private static Task<bool> InvokeFetchAndApply(ConfigurationPoller poller, InstrumentationType type)
    {
        var method = typeof(ConfigurationPoller).GetMethod(
            "FetchAndApply", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<bool>)method.Invoke(poller, new object[] { type })!;
    }

    private static HashSet<string> GetLastFingerprint(ConfigurationPoller poller)
    {
        var field = typeof(ConfigurationPoller).GetField(
            "lastFingerprint", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (HashSet<string>)field.GetValue(poller)!;
    }

    private static string ProbeConfig(string locationHash) =>
        "{ \"InstrumentationType\": \"PROBE\", \"LocationHash\": \"" + locationHash + "\", " +
        "\"Location\": { \"CodeLocation\": { \"Language\": \"Dotnet\", \"ClassName\": \"OrderService\", \"MethodName\": \"Process\" } } }";

    private static string ChangedResponse(params string[] configs) =>
        "{ \"Changed\": true, \"SyncedAt\": \"2024-09-17T22:03:24Z\", \"LatestConfigurations\": [" +
        string.Join(",", configs) + "] }";
}

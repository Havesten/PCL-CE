using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.EasyTier;

namespace PCL.Core.Test;

[TestClass]
public class P2pConnectionRecoveryTest
{
    [TestMethod]
    public void GuestStagesDescribeDiscoveryDirectConnectionAndGameValidation()
    {
        var recovery = new P2pConnectionRecovery(false);
        Assert.AreEqual(P2pConnectionPhase.WaitingHost, Observe(recovery, 0).Phase);
        Assert.AreEqual(P2pConnectionPhase.Connecting, Observe(recovery, 1, peer: true).Phase);
        Assert.AreEqual(P2pConnectionPhase.Validating, Observe(recovery, 2, peer: true, direct: true).Phase);
        var connected = Observe(recovery, 3, peer: true, direct: true, game: true);
        Assert.AreEqual(P2pConnectionPhase.Connected, connected.Phase);
        Assert.IsFalse(connected.Restart);
        Assert.IsNull(connected.FailureCode);
    }

    [TestMethod]
    [DataRow(false, false, "process_exited")]
    [DataRow(true, false, "connect_timeout")]
    [DataRow(true, true, "game_unavailable")]
    public void InitialAttemptHasSixtySecondDeadlineWithSpecificFailure(bool process, bool direct, string failure)
    {
        var recovery = new P2pConnectionRecovery(false);
        Assert.AreNotEqual(P2pConnectionPhase.Failed, Observe(recovery, 59, process, true, direct).Phase);
        var result = Observe(recovery, 60, process, true, direct);
        Assert.AreEqual(P2pConnectionPhase.Failed, result.Phase);
        Assert.AreEqual(failure, result.FailureCode);
        Assert.IsFalse(result.Restart);
    }

    [TestMethod]
    public void HealthyHostWithoutPlayersRemainsHostingForAnyDuration()
    {
        var recovery = new P2pConnectionRecovery(true);
        foreach (var seconds in new[] { 0, 10, 60, 120, 3600 })
        {
            var result = Observe(recovery, seconds);
            Assert.AreEqual(P2pConnectionPhase.Hosting, result.Phase);
            Assert.IsFalse(result.Restart);
            Assert.AreEqual(0, result.RetryCount);
        }
    }

    [TestMethod]
    public void ReadyGuestDoesNotRestartOnPeriodicHealthyChecks()
    {
        var recovery = new P2pConnectionRecovery(false);
        foreach (var seconds in new[] { 0, 10, 25, 45, 120, 3600 })
        {
            var result = Observe(recovery, seconds, peer: true, direct: true, game: true);
            Assert.AreEqual(P2pConnectionPhase.Connected, result.Phase);
            Assert.IsFalse(result.Restart);
        }
    }

    [TestMethod]
    public void RecoverySchedulesThreeRestartsAndTwoMinutesFromFirstLoss()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 5, peer: true, direct: true, game: true);
        Assert.AreEqual(P2pConnectionPhase.Reconnecting, Observe(recovery, 6).Phase);
        Assert.IsFalse(Observe(recovery, 15).Restart);
        var first = Observe(recovery, 16);
        Assert.IsTrue(first.Restart); Assert.AreEqual(1, first.RetryCount);
        Assert.IsFalse(Observe(recovery, 30).Restart);
        var second = Observe(recovery, 31);
        Assert.IsTrue(second.Restart); Assert.AreEqual(2, second.RetryCount);
        var third = Observe(recovery, 51);
        Assert.IsTrue(third.Restart); Assert.AreEqual(3, third.RetryCount);
        Assert.IsFalse(Observe(recovery, 100).Restart);
        Assert.AreNotEqual(P2pConnectionPhase.Failed, Observe(recovery, 125).Phase);
        Assert.AreEqual(P2pConnectionPhase.Failed, Observe(recovery, 126).Phase);
    }

    [TestMethod]
    public void NetworkChangesCannotResetRecoveryDeadlineOrExceedRetryCap()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 0, peer: true, direct: true, game: true);
        foreach (var seconds in new[] { 1, 11, 21 })
            Assert.IsTrue(Observe(recovery, seconds, peer: true, direct: true, game: true, changed: true).Restart);
        var capped = Observe(recovery, 31, peer: true, direct: true, game: true, changed: true);
        Assert.IsFalse(capped.Restart); Assert.AreEqual(3, capped.RetryCount);
        Assert.AreEqual(P2pConnectionPhase.Failed, Observe(recovery, 121, peer: true, direct: true, game: true, changed: true).Phase);
    }

    [TestMethod]
    public void SuccessfulReconnectionResetsBudgetForNewIndependentLoss()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 1, peer: true, direct: true, game: true);
        Observe(recovery, 2);
        Assert.IsTrue(Observe(recovery, 12).Restart);
        Observe(recovery, 13, peer: true, direct: true, game: true);
        var restored = Observe(recovery, 28, peer: true, direct: true, game: true);
        Assert.AreEqual(P2pConnectionPhase.Connected, restored.Phase);
        Assert.AreEqual(0, restored.RetryCount);
        Observe(recovery, 30);
        var next = Observe(recovery, 40);
        Assert.IsTrue(next.Restart); Assert.AreEqual(1, next.RetryCount);
        Assert.AreNotEqual(P2pConnectionPhase.Failed, Observe(recovery, 149).Phase);
        Assert.AreEqual(P2pConnectionPhase.Failed, Observe(recovery, 150).Phase);
    }

    [TestMethod]
    public void HostProcessFailureEntersBoundedRecovery()
    {
        var recovery = new P2pConnectionRecovery(true);
        Observe(recovery, 0);
        Assert.AreEqual(P2pConnectionPhase.Reconnecting, Observe(recovery, 1, process: false).Phase);
        Assert.AreEqual("process_exited", Observe(recovery, 121, process: false).FailureCode);
    }

    [TestMethod]
    public void RapidNetworkEventsAreMergedWithinTenSeconds()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 0, peer: true, direct: true, game: true);
        Assert.IsTrue(Observe(recovery, 1, peer: true, direct: true, game: true, changed: true).Restart);
        for (var seconds = 2; seconds < 11; seconds++)
        {
            var merged = Observe(recovery, seconds, peer: true, direct: true, game: true, changed: true);
            Assert.IsFalse(merged.Restart);
            Assert.AreEqual(1, merged.RetryCount);
        }
        Assert.IsTrue(Observe(recovery, 11, peer: true, direct: true, game: true, changed: true).Restart);
    }

    [TestMethod]
    public void AlternatingBriefHealthySamplesAndNetworkEventsCannotRestartForever()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 0, peer: true, direct: true, game: true);
        var restarts = 0;
        for (var seconds = 1; seconds <= 120; seconds++)
        {
            var sample = Observe(recovery, seconds, peer: true, direct: true, game: true, changed: seconds % 5 == 1);
            if (sample.Restart) restarts++;
            Assert.IsTrue(restarts <= 3, "Brief ready samples reset the retry cap during repeated network changes.");
        }
        Assert.AreEqual(P2pConnectionPhase.Failed, Observe(recovery, 121, peer: true, direct: true, game: true, changed: true).Phase);
    }

    [TestMethod]
    public void OfflineRecoveryDoesNotConsumeRestartBudgetButStillExpires()
    {
        var recovery = new P2pConnectionRecovery(false);
        Observe(recovery, 0, peer: true, direct: true, game: true);
        for (var seconds = 1; seconds < 120; seconds += 10)
        {
            var result = recovery.Observe(TimeSpan.FromSeconds(seconds), false, false, false, false, true, canRestart: false);
            Assert.IsFalse(result.Restart);
            Assert.AreEqual(0, result.RetryCount);
        }
        var failed = recovery.Observe(TimeSpan.FromSeconds(121), false, false, false, false, true, canRestart: false);
        Assert.AreEqual(P2pConnectionPhase.Failed, failed.Phase);
        Assert.AreEqual(0, failed.RetryCount);
    }

    private static P2pRecoveryDecision Observe(P2pConnectionRecovery recovery, int seconds, bool process = true,
        bool peer = false, bool direct = false, bool game = false, bool changed = false) =>
        recovery.Observe(TimeSpan.FromSeconds(seconds), process, peer, direct, game, changed);
}

using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityRewardAlignmentPatchTests
{
    [TestCleanup]
    public void Cleanup()
    {
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void RewardBoundaryRealignsRememberedBrokerRunAfterRewardSynchronizerDrifts()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);
        var owner = new FakeRunManagerOwner(service)
        {
            RewardSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1))
        };

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            Assert.IsTrue(RunIdentityLaunchPatch.AlignLocalContextForBrokerRunForTesting(owner));
            owner.RewardSynchronizer.SetLocalPlayerId(BrokerPlayerId.ForClientIndex(1));
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityRewardAlignmentPatch.AlignRunIdentityForRewardBoundaryForTesting(null);

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.RewardSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RewardBoundaryLeavesStateUnchangedWithoutRememberedBrokerRun()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityRewardAlignmentPatch.AlignRunIdentityForRewardBoundaryForTesting(null);

            Assert.IsFalse(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RewardBoundaryDoesNotReplaceRememberedRunWithBoundaryServiceOwner()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);
        var owner = new FakeRunManagerOwner(service)
        {
            RewardSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(0))
        };

        try
        {
            Assert.IsTrue(RunIdentityLaunchPatch.AlignLocalContextForBrokerRunForTesting(owner));
            owner.RewardSynchronizer.SetLocalPlayerId(BrokerPlayerId.ForClientIndex(1));
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityRewardAlignmentPatch.AlignRunIdentityForRewardBoundaryForTesting(
                new FakeBoundaryServiceOwner(service));

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.RewardSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RewardAlignmentPatchTargetsRewardAndPotionBoundaries()
    {
        CollectionAssert.Contains(
            RunIdentityRewardAlignmentPatch.TargetSignaturesForTesting.ToArray(),
            ("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton", "GetReward"));
        CollectionAssert.Contains(
            RunIdentityRewardAlignmentPatch.TargetSignaturesForTesting.ToArray(),
            ("MegaCrit.Sts2.Core.Commands.PotionCmd", "TryToProcure"));
        CollectionAssert.Contains(
            RunIdentityRewardAlignmentPatch.TargetSignaturesForTesting.ToArray(),
            ("MegaCrit.Sts2.Core.Multiplayer.Game.RewardSynchronizer", "SyncLocalObtainedPotion"));
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public FakeLocalPlayerSynchronizer? RewardSynchronizer { get; init; }
    }

    private sealed class FakeBoundaryServiceOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;
    }

    private sealed class FakeLocalPlayerSynchronizer(ulong localPlayerId)
    {
        private ulong _localPlayerId = localPlayerId;

        public ulong LocalPlayerId => _localPlayerId;

        public void SetLocalPlayerId(ulong localPlayerId)
        {
            _localPlayerId = localPlayerId;
        }
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<BrokerEnvelope?>(null);
        }
    }
}

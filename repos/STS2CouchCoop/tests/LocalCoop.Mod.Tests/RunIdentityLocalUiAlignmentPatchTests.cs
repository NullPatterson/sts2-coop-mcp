using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityLocalUiAlignmentPatchTests
{
    [TestCleanup]
    public void Cleanup()
    {
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void LocalUiAlignmentTargetsEventRewardAndShopUiBoundaries()
    {
        var targets = RunIdentityLocalUiAlignmentPatch.TargetSignaturesForTesting.ToArray();

        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer", "GetLocalEvent"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer", "ChooseLocalOption"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Rooms.EventRoom", "EnterInternal"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "Create"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "SetOptions"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen", "SetRewards"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton", "GetReward"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer", "DoLocalMerchantCardRemoval"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Commands.PotionCmd", "TryToProcure"));
    }

    [TestMethod]
    public void LocalUiAlignmentRealignsRememberedBrokerRunBeforeEventUiSelection()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);
        var owner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            RewardSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            OneOffSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1))
        };

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(owner));
            owner.EventSynchronizer.SetLocalPlayerId(BrokerPlayerId.ForClientIndex(1));
            owner.RewardSynchronizer.SetLocalPlayerId(BrokerPlayerId.ForClientIndex(1));
            owner.OneOffSynchronizer.SetLocalPlayerId(BrokerPlayerId.ForClientIndex(1));
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityLocalUiAlignmentPatch.AlignLocalUiForTesting(null);

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.EventSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.RewardSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.OneOffSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void LocalUiAlignmentLeavesStateUnchangedWithoutRememberedBrokerRun()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityLocalUiAlignmentPatch.AlignLocalUiForTesting(null);

            Assert.IsFalse(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public required FakeLocalPlayerSynchronizer EventSynchronizer { get; init; }

        public required FakeLocalPlayerSynchronizer RewardSynchronizer { get; init; }

        public required FakeLocalPlayerSynchronizer OneOffSynchronizer { get; init; }
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

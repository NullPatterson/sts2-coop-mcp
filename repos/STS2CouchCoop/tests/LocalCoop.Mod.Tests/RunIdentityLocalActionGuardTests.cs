using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityLocalActionGuardTests
{
    [TestCleanup]
    public void Cleanup()
    {
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void AllowsLocalUnfinishedEventOptionForLocalPlayer()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);
            var button = new FakeEventOptionButton(
                new FakeEventOption(
                    new FakePlayer(BrokerPlayerId.ForClientIndex(0)),
                    new FakeEvent(isFinished: false)));

            Assert.IsTrue(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(button, []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesLocalEventOptionForRemotePlayer()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);
            var button = new FakeEventOptionButton(
                new FakeEventOption(
                    new FakePlayer(BrokerPlayerId.ForClientIndex(1)),
                    new FakeEvent(isFinished: false)));

            Assert.IsFalse(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(button, []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesAlreadyFinishedLocalEventOption()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);
            var button = new FakeEventOptionButton(
                new FakeEventOption(
                    new FakePlayer(BrokerPlayerId.ForClientIndex(0)),
                    new FakeEvent(isFinished: true)));

            Assert.IsFalse(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(button, []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void AllowsUnknownEventOptionShape()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            Assert.IsTrue(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(new object(), []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesLocalRewardActionForRemotePlayer()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);
            var button = new FakeRewardButton(new FakeReward(new FakePlayer(BrokerPlayerId.ForClientIndex(1))));

            Assert.IsFalse(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(button, []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesLocalShopRemoveActionForRemotePlayer()
    {
        var previousNetId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);
            var shopRemove = new FakeShopRemoveAction(new FakePlayer(BrokerPlayerId.ForClientIndex(1)));

            Assert.IsFalse(RunIdentityLocalActionGuard.ShouldAllowLocalActionForTesting(shopRemove, []));
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void LocalActionGuardTargetsOnlyLocalUiActionBoundaries()
    {
        var targets = RunIdentityLocalActionGuardPatch.TargetSignaturesForTesting.ToArray();

        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton", "OnRelease"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer", "ChooseLocalOption"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton", "OnRelease"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Commands.PotionCmd", "TryToProcure"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer", "DoLocalMerchantCardRemoval"));
        CollectionAssert.DoesNotContain(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer", "HandleEventOptionChosenMessage"));
        CollectionAssert.DoesNotContain(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.RewardSynchronizer", "HandleRewardObtainedMessage"));
        CollectionAssert.DoesNotContain(
            targets,
            ("MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer", "HandleMerchantCardRemoval"));
    }

    private sealed class FakeEventOptionButton(FakeEventOption eventOption)
    {
        public FakeEventOption EventOption { get; } = eventOption;
    }

    private sealed class FakeEventOption(FakePlayer player, FakeEvent eventModel)
    {
        public FakePlayer Player { get; } = player;

        public FakeEvent Event { get; } = eventModel;
    }

    private sealed class FakePlayer(ulong netId)
    {
        public ulong NetId { get; } = netId;
    }

    private sealed class FakeEvent(bool isFinished)
    {
        public bool IsFinished { get; } = isFinished;
    }

    private sealed class FakeRewardButton(FakeReward reward)
    {
        public FakeReward Reward { get; } = reward;
    }

    private sealed class FakeReward(FakePlayer player)
    {
        public FakePlayer Player { get; } = player;
    }

    private sealed class FakeShopRemoveAction(FakePlayer player)
    {
        public FakePlayer Player { get; } = player;
    }
}

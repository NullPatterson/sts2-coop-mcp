using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityLaunchPatchTests
{
    [TestMethod]
    public void LaunchPatchAlignsLocalContextToBrokerNetService()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-1", 1, new CapturingTransport()),
            NetGameType.Client);

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var aligned = RunIdentityLaunchPatch.AlignLocalContextForBrokerRunForTesting(
                new FakeRunManagerOwner(service));

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void LaunchPatchAlignsEventSynchronizerLocalPlayerToBrokerNetService()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var synchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1));
            var aligned = RunIdentityLaunchPatch.AlignLocalContextForBrokerRunForTesting(
                new FakeRunManagerOwner(service, synchronizer));

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), synchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RunIdentityAlignmentAlignsAllNativeSynchronizerLocalPlayersToBrokerNetService()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);
        var owner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            OneOffSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            RewardSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            RestSiteSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            FlavorSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1)),
            TreasureRoomRelicSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1))
        };

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            var aligned = RunIdentityAlignment.AlignBrokerRun(owner);

            Assert.IsTrue(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.EventSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.OneOffSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.RewardSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.RestSiteSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.FlavorSynchronizer.LocalPlayerId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.TreasureRoomRelicSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RunIdentityAlignmentIsIdempotent()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-1", 1, new CapturingTransport()),
            NetGameType.Host);
        var owner = new FakeRunManagerOwner(service)
        {
            OneOffSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(0))
        };

        try
        {
            Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(owner));
            Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(owner));

            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), owner.OneOffSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RunIdentityAlignmentLeavesNonBrokerRunUnchanged()
    {
        var previousNetId = LocalContext.NetId;
        var synchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1));

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var aligned = RunIdentityAlignment.AlignBrokerRun(new { OneOffSynchronizer = synchronizer });

            Assert.IsFalse(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), synchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void LaunchPatchDoesNotChangeLocalContextWithoutBrokerNetService()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var aligned = RunIdentityLaunchPatch.AlignLocalContextForBrokerRunForTesting(new object());

            Assert.IsFalse(aligned);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void RunIdentityLaunchPatchTargetsRunManagerLaunch()
    {
        Assert.AreEqual(
            ("MegaCrit.Sts2.Core.Runs.RunManager", "Launch"),
            RunIdentityLaunchPatch.TargetSignatureForTesting);
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService, object? eventSynchronizer = null)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public FakeLocalPlayerSynchronizer? EventSynchronizer { get; init; } = eventSynchronizer as FakeLocalPlayerSynchronizer;

        public FakeLocalPlayerSynchronizer? OneOffSynchronizer { get; init; }

        public FakeLocalPlayerSynchronizer? RewardSynchronizer { get; init; }

        public FakeLocalPlayerSynchronizer? RestSiteSynchronizer { get; init; }

        public FakeLocalPlayerSynchronizer? FlavorSynchronizer { get; init; }

        public FakeLocalPlayerSynchronizer? TreasureRoomRelicSynchronizer { get; init; }
    }

    private sealed class FakeLocalPlayerSynchronizer(ulong localPlayerId)
    {
        private ulong _localPlayerId = localPlayerId;

        public ulong LocalPlayerId => _localPlayerId;
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

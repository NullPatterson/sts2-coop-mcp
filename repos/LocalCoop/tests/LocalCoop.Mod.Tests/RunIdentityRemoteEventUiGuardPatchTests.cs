using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityRemoteEventUiGuardPatchTests
{
    [TestCleanup]
    public void Cleanup()
    {
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void RemoteEventUiGuardTargetsEventRoomCreateAndSetOptions()
    {
        var targets = RunIdentityRemoteEventUiGuardPatch.TargetSignaturesForTesting.ToArray();

        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "Create"));
        CollectionAssert.Contains(
            targets,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "SetOptions"));
    }

    [TestMethod]
    public void RemoteEventUiGuardResolvesNativeEventRoomMethods()
    {
        var methods = RunIdentityRemoteEventUiGuardPatch.TargetMethods()
            .Select(method => (method.DeclaringType?.FullName, method.Name))
            .ToArray();

        CollectionAssert.Contains(
            methods,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "Create"));
        CollectionAssert.Contains(
            methods,
            ("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "SetOptions"));
    }

    [TestMethod]
    public void RebindsRemoteEventArgumentToRememberedLocalEvent()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);
        var localEvent = new FakeEvent(new FakePlayer(BrokerPlayerId.ForClientIndex(0)));
        var remoteEvent = new FakeEvent(new FakePlayer(BrokerPlayerId.ForClientIndex(1)));
        var owner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeEventSynchronizer(BrokerPlayerId.ForClientIndex(1), localEvent)
        };
        object?[] args = [remoteEvent, new object(), false];

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);
            Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(owner));

            var rebound = RunIdentityRemoteEventUiGuardPatch.RebindRemoteEventUiForTesting(null, args);

            Assert.IsTrue(rebound);
            Assert.AreSame(localEvent, args[0]);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), owner.EventSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void LeavesLocalEventArgumentUnchanged()
    {
        var previousNetId = LocalContext.NetId;
        var localEvent = new FakeEvent(new FakePlayer(BrokerPlayerId.ForClientIndex(0)));
        object?[] args = [localEvent];

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var rebound = RunIdentityRemoteEventUiGuardPatch.RebindRemoteEventUiForTesting(null, args);

            Assert.IsFalse(rebound);
            Assert.AreSame(localEvent, args[0]);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public required FakeEventSynchronizer EventSynchronizer { get; init; }
    }

    private sealed class FakeEventSynchronizer(ulong localPlayerId, FakeEvent localEvent)
    {
        private ulong _localPlayerId = localPlayerId;

        public ulong LocalPlayerId => _localPlayerId;

        public FakeEvent GetLocalEvent()
        {
            return localEvent;
        }
    }

    private sealed class FakeEvent(FakePlayer player)
    {
        public FakePlayer Player { get; } = player;

        public string Id => "EVENT.NEOW";
    }

    private sealed class FakePlayer(ulong netId)
    {
        public ulong NetId { get; } = netId;
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

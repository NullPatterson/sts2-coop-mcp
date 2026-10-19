using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityDualRoleAdventureGuardPatchTests
{
    private object? _rememberedOwner;

    [TestCleanup]
    public void Cleanup()
    {
        _rememberedOwner = null;
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void DualRoleAdventureGuardTargetsLocalSelfCoopSwitchAndVisualBoundaries()
    {
        var targets = RunIdentityDualRoleAdventureGuardTargets.TargetSignaturesForTesting.ToArray();

        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Patch.LocalMultiControlPatch", "PostfixRunManagerLaunch"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalSelfCoopContext", "get_IsEnabled"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalSelfCoopContext", "get_UseSingleEventFlow"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime", "ApplyControlContext"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime", "TryRunPendingEventAutoSwitch"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime", "RefreshTopBarDeck"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Runtime.LocalControlSwitchGuard", "TrySwitchTo"));
        CollectionAssert.Contains(
            targets,
            ("LocalMultiControl.Scripts.Patch.NEventRoomPatch", "TryAutoSwitchToNextPendingEvent"));
    }

    [TestMethod]
    public void DualRoleAdventureGuardIsOptionalWhenDualRoleAssemblyIsAbsent()
    {
        Assert.IsFalse(RunIdentityDualRoleAdventureVoidGuardPatch.TargetMethods().Any());
        Assert.IsFalse(RunIdentityDualRoleAdventureBoolGuardPatch.TargetMethods().Any());
    }

    [TestMethod]
    public void SuppressesDualRoleLocalSelfCoopWhenRememberedBrokerRunExists()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);

            var shouldSuppress = RunIdentityDualRoleAdventureGuard.ShouldSuppressForTesting(null, []);

            Assert.IsTrue(shouldSuppress);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesDualRoleLocalSelfCoopWhenRunManagerArgumentExposesBrokerService()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-1", 1, new CapturingTransport()),
            NetGameType.Client);
        var owner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(0))
        };

        try
        {
            LocalContext.NetId = 0;

            var shouldSuppress = RunIdentityDualRoleAdventureGuard.ShouldSuppressForTesting(null, [owner]);

            Assert.IsTrue(shouldSuppress);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), owner.EventSynchronizer.LocalPlayerId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void DoesNotSuppressDualRoleAdventureOutsideBrokerRuns()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = 1234;

            var shouldSuppress = RunIdentityDualRoleAdventureGuard.ShouldSuppressForTesting(null, []);

            Assert.IsFalse(shouldSuppress);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesWhenLocalContextAlreadyCarriesBrokerNetId()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(0);

            var shouldSuppress = RunIdentityDualRoleAdventureGuard.ShouldSuppressForTesting(null, []);

            Assert.IsTrue(shouldSuppress);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    private void RememberBrokerRun(BrokerNetGameService service)
    {
        _rememberedOwner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1))
        };

        Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(_rememberedOwner));
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public required FakeLocalPlayerSynchronizer EventSynchronizer { get; init; }
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

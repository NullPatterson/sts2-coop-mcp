using System.Reflection;
using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityPotionAnimationGuardPatchTests
{
    private object? _rememberedOwner;

    [TestCleanup]
    public void Cleanup()
    {
        _rememberedOwner = null;
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void TargetMethodResolvesNativePotionContainerAnimatePotionReturningVoid()
    {
        var target = RunIdentityPotionAnimationGuardPatch.TargetMethod();

        Assert.IsNotNull(target);
        Assert.AreEqual("AnimatePotion", target!.Name);
        Assert.AreEqual(
            "MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer",
            target.DeclaringType?.FullName);
        Assert.AreEqual(typeof(void), ((MethodInfo)target).ReturnType);
    }

    [TestMethod]
    public void SuppressesKnownPotionAnimationFaultWhenBrokerRunRemembered()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-1", 1, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);

            var result = RunIdentityPotionAnimationGuardPatch.FilterPotionAnimationExceptionForTesting(
                new InvalidOperationException("Sequence contains no matching element"));

            Assert.IsNull(result);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void PropagatesKnownPotionAnimationFaultWithoutBrokerRun()
    {
        var previousNetId = LocalContext.NetId;

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);
            var expected = new InvalidOperationException("Sequence contains no matching element");

            var result = RunIdentityPotionAnimationGuardPatch.FilterPotionAnimationExceptionForTesting(expected);

            Assert.AreSame(expected, result);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void PropagatesUnexpectedPotionAnimationFaults()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Client);

        try
        {
            RememberBrokerRun(service);
            var expected = new InvalidOperationException("different potion animation failure");

            var result = RunIdentityPotionAnimationGuardPatch.FilterPotionAnimationExceptionForTesting(expected);

            Assert.AreSame(expected, result);
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
            RewardSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(0))
        };

        Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(_rememberedOwner));
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public required FakeLocalPlayerSynchronizer RewardSynchronizer { get; init; }
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

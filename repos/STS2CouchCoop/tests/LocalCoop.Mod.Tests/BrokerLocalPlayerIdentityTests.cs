using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerLocalPlayerIdentityTests
{
    [TestMethod]
    public void TryGetLocalPlayerIdUsesBrokerClientIndexWhenBrokerModeIsEnabled()
    {
        var settings = EnabledSettings(clientIndex: 2);

        var resolved = BrokerLocalPlayerIdentity.TryGetLocalPlayerId(settings, out var playerId);

        Assert.IsTrue(resolved);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(2), playerId);
    }

    [TestMethod]
    public void TryGetLocalPlayerIdFallsThroughWhenBrokerModeIsDisabled()
    {
        var settings = new BrokerModeSettings(false, null, "client-0", "events.txt", null);

        var resolved = BrokerLocalPlayerIdentity.TryGetLocalPlayerId(settings, out var playerId);

        Assert.IsFalse(resolved);
        Assert.AreEqual(0UL, playerId);
    }

    [TestMethod]
    public void PatchTargetsNativePlatformLocalPlayerIdLookup()
    {
        var target = LocalCoop.Mod.Patches.BrokerLocalPlayerIdPatch.TargetMethod();

        Assert.IsNotNull(target);
        Assert.AreEqual("GetLocalPlayerId", target!.Name);
        Assert.AreEqual("MegaCrit.Sts2.Core.Platform.PlatformUtil", target.DeclaringType?.FullName);
    }

    private static BrokerModeSettings EnabledSettings(int clientIndex)
    {
        return new BrokerModeSettings(
            true,
            new BrokerClientConfig(BrokerClientRole.Client, clientIndex, "127.0.0.1", 38989, "local-test"),
            $"client-{clientIndex}",
            "events.txt",
            null);
    }
}

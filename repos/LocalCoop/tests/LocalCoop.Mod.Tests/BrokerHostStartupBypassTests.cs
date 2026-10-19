using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerHostStartupBypassTests
{
    [TestMethod]
    public void ShouldSkipNativeHostStartupWhenBrokerModeIsEnabled()
    {
        Assert.IsTrue(BrokerHostStartupBypass.ShouldSkipNativeHostStartup(EnabledSettings()));
    }

    [TestMethod]
    public void ShouldAllowNativeHostStartupWhenBrokerModeIsDisabled()
    {
        var settings = new BrokerModeSettings(false, null, "client-0", "events.txt", null);

        Assert.IsFalse(BrokerHostStartupBypass.ShouldSkipNativeHostStartup(settings));
    }

    [TestMethod]
    public void TrySkipNativeHostStartupLogsSteamBypass()
    {
        var messages = new List<string>();

        var skipped = BrokerHostStartupBypass.TrySkipNativeHostStartup(
            EnabledSettings(),
            "StartSteamHost",
            messages.Add);

        Assert.IsTrue(skipped);
        CollectionAssert.Contains(messages, "Broker host startup bypass: skipped native StartSteamHost in broker mode.");
    }

    [TestMethod]
    public void TrySkipNativeHostStartupLogsENetBypass()
    {
        var messages = new List<string>();

        var skipped = BrokerHostStartupBypass.TrySkipNativeHostStartup(
            EnabledSettings(),
            "StartENetHost",
            messages.Add);

        Assert.IsTrue(skipped);
        CollectionAssert.Contains(messages, "Broker host startup bypass: skipped native StartENetHost in broker mode.");
    }

    [TestMethod]
    public void TrySkipNativeHostStartupDoesNotLogWhenBrokerModeIsDisabled()
    {
        var messages = new List<string>();
        var settings = new BrokerModeSettings(false, null, "client-0", "events.txt", null);

        var skipped = BrokerHostStartupBypass.TrySkipNativeHostStartup(
            settings,
            "StartSteamHost",
            messages.Add);

        Assert.IsFalse(skipped);
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void SteamHostStartupBypassPatchTargetsNativeSteamHostStartup()
    {
        var target = LocalCoop.Mod.Patches.BrokerHostSteamStartupBypassPatch.TargetMethod();

        Assert.IsNotNull(target);
        Assert.AreEqual("StartSteamHost", target!.Name);
        Assert.AreEqual("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService", target.DeclaringType?.FullName);
    }

    [TestMethod]
    public void ENetHostStartupBypassPatchTargetsNativeENetHostStartup()
    {
        var target = LocalCoop.Mod.Patches.BrokerHostENetStartupBypassPatch.TargetMethod();

        Assert.IsNotNull(target);
        Assert.AreEqual("StartENetHost", target!.Name);
        Assert.AreEqual("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService", target.DeclaringType?.FullName);
    }

    private static BrokerModeSettings EnabledSettings()
    {
        return new BrokerModeSettings(
            true,
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", 38989, "local-test"),
            "client-0",
            "events.txt",
            null);
    }
}

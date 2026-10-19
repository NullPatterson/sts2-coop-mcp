using LocalCoop.MultiClientHarness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class TwoClientHarnessPreparationTests
{
    [TestMethod]
    public void PrepareWritesFourConfigsAndReturnsBrokerAndLaunchCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalCoopHarnessTests", Guid.NewGuid().ToString("N"));

        var result = ClientHarnessPreparation.Prepare(
            root,
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
            "local-test",
            "127.0.0.1",
            38989,
            clientCount: 4,
            controllerDevices: "0,1,none,3");

        Assert.AreEqual(4, result.ConfigSetup.Clients.Count);
        Assert.IsTrue(File.Exists(Path.Combine(result.ConfigSetup.Clients[0].Directory, "enable-local-broker.txt")));
        StringAssert.Contains(result.BrokerCommand, "LocalCoop.Broker.Cli");
        StringAssert.Contains(result.BrokerCommand, "local-test");
        Assert.AreEqual(4, result.LaunchCommands.Count);
        StringAssert.Contains(result.LaunchCommands[0], result.ConfigSetup.Clients[0].Directory);
        StringAssert.Contains(result.LaunchCommands[3], result.ConfigSetup.Clients[3].Directory);

        var client2Config = File.ReadAllText(Path.Combine(result.ConfigSetup.Clients[2].Directory, "enable-local-broker.txt"));
        StringAssert.Contains(client2Config, "playerSlot=2");
        StringAssert.Contains(client2Config, "inputMode=none");
    }

    [TestMethod]
    public void PrepareWritesTwoConfigsAndReturnsBrokerAndLaunchCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalCoopHarnessTests", Guid.NewGuid().ToString("N"));

        var result = TwoClientHarnessPreparation.Prepare(
            root,
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
            "local-test",
            "127.0.0.1",
            38989);

        Assert.AreEqual(2, result.ConfigSetup.Clients.Count);
        Assert.IsTrue(File.Exists(Path.Combine(result.ConfigSetup.Clients[0].Directory, "enable-local-broker.txt")));
        StringAssert.Contains(result.BrokerCommand, "LocalCoop.Broker.Cli");
        StringAssert.Contains(result.BrokerCommand, "local-test");
        Assert.AreEqual(2, result.LaunchCommands.Count);
        StringAssert.Contains(result.LaunchCommands[0], result.ConfigSetup.Clients[0].Directory);
        StringAssert.Contains(result.LaunchCommands[1], result.ConfigSetup.Clients[1].Directory);
    }
}

using LocalCoop.MultiClientHarness;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class ClientLaunchPlanTests
{
    [TestMethod]
    public void CreatesFourSameMachineBrokerConfigs()
    {
        var plan = ClientLaunchPlan.CreateDefault("local-test", "127.0.0.1", 38989);

        Assert.AreEqual(4, plan.Clients.Count);
        CollectionAssert.AreEqual(new[] { "client-0", "client-1", "client-2", "client-3" }, plan.Clients.Select(client => client.ClientId).ToArray());

        var parsed = plan.Clients
            .Select(client => BrokerClientConfig.Parse(client.ConfigContent))
            .ToArray();

        Assert.AreEqual(BrokerClientRole.Host, parsed[0].Role);
        Assert.IsTrue(parsed.Skip(1).All(config => config.Role == BrokerClientRole.Client));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, parsed.Select(config => config.ClientIndex).ToArray());
        Assert.IsTrue(parsed.All(config => !config.ControllerDevice.IsConfigured));
        Assert.IsTrue(parsed.All(config => config.SessionId == "local-test"));
        Assert.IsTrue(parsed.All(config => config.Port == 38989));
        StringAssert.Contains(plan.Clients[0].ConfigContent, "playerSlot=0");
        StringAssert.Contains(plan.Clients[0].ConfigContent, "inputMode=auto");
        StringAssert.Contains(plan.Clients[0].ConfigContent, "controllerClientCount=4");
        Assert.IsFalse(plan.Clients[0].ConfigContent.Contains("controllerDevice=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreatesTwoClientBrokerConfigsForLobbySmoke()
    {
        var plan = ClientLaunchPlan.CreateTwoClient("local-test", "127.0.0.1", 38989);

        Assert.AreEqual(2, plan.Clients.Count);
        CollectionAssert.AreEqual(new[] { "client-0", "client-1" }, plan.Clients.Select(client => client.ClientId).ToArray());

        var parsed = plan.Clients
            .Select(client => BrokerClientConfig.Parse(client.ConfigContent))
            .ToArray();

        Assert.AreEqual(BrokerClientRole.Host, parsed[0].Role);
        Assert.AreEqual(BrokerClientRole.Client, parsed[1].Role);
        CollectionAssert.AreEqual(new[] { 0, 1 }, parsed.Select(config => config.ClientIndex).ToArray());
        Assert.IsTrue(parsed.All(config => !config.ControllerDevice.IsConfigured));
        Assert.IsFalse(plan.Clients[1].ConfigContent.Contains("controllerDevice=", StringComparison.Ordinal));
        StringAssert.Contains(plan.Clients[1].ConfigContent, "controllerClientCount=2");
    }

    [TestMethod]
    public void CreatesThreeClientBrokerConfigsWithControllerOverrides()
    {
        var plan = ClientLaunchPlan.Create(
            "local-test",
            "127.0.0.1",
            38989,
            clientCount: 3,
            controllerDevices:
            [
                BrokerControllerDeviceAssignment.ForDevice(2),
                BrokerControllerDeviceAssignment.None,
                BrokerControllerDeviceAssignment.ForDevice(0)
            ]);

        CollectionAssert.AreEqual(new[] { "client-0", "client-1", "client-2" }, plan.Clients.Select(client => client.ClientId).ToArray());

        var parsed = plan.Clients
            .Select(client => BrokerClientConfig.Parse(client.ConfigContent))
            .ToArray();

        Assert.AreEqual(BrokerClientRole.Host, parsed[0].Role);
        Assert.IsTrue(parsed.Skip(1).All(config => config.Role == BrokerClientRole.Client));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, parsed.Select(config => config.ClientIndex).ToArray());
        CollectionAssert.AreEqual(new[] { false, true, false }, parsed.Select(config => config.ControllerDevice.IsConfigured).ToArray());
        Assert.IsTrue(parsed.All(config => config.ControllerDevice.Device is null));
        StringAssert.Contains(plan.Clients[0].ConfigContent, "playerSlot=0");
        StringAssert.Contains(plan.Clients[0].ConfigContent, "controllerClientCount=2");
        Assert.IsFalse(plan.Clients[0].ConfigContent.Contains("controllerDevice=", StringComparison.Ordinal));
        StringAssert.Contains(plan.Clients[1].ConfigContent, "playerSlot=1");
        StringAssert.Contains(plan.Clients[1].ConfigContent, "inputMode=none");
        StringAssert.Contains(plan.Clients[1].ConfigContent, "controllerClientCount=2");
        Assert.IsFalse(plan.Clients[1].ConfigContent.Contains("controllerDevice=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RejectsClientCountsOutsideTwoThroughFour()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            ClientLaunchPlan.Create("local-test", "127.0.0.1", 38989, clientCount: 1));

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            ClientLaunchPlan.Create("local-test", "127.0.0.1", 38989, clientCount: 5));
    }

    [TestMethod]
    public void RejectsControllerOverrideCountMismatch()
    {
        var exception = Assert.ThrowsException<ArgumentException>(() =>
            ClientLaunchPlan.Create(
                "local-test",
                "127.0.0.1",
                38989,
                clientCount: 3,
                controllerDevices:
                [
                    BrokerControllerDeviceAssignment.ForDevice(0),
                    BrokerControllerDeviceAssignment.ForDevice(1)
                ]));

        StringAssert.Contains(exception.Message, "client count");
    }
}

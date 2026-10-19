using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class ControllerAssignmentServiceTests
{
    [TestMethod]
    public void ResolvesCanonicalAutoAssignmentFromPlayerSlot()
    {
        var assignment = ControllerAssignmentService.Resolve(new BrokerClientConfig(
            BrokerClientRole.Client,
            ClientIndex: 1,
            Host: "127.0.0.1",
            Port: 38989,
            SessionId: "local-test",
            PlayerSlot: 2,
            InputMode: BrokerClientInputMode.Auto,
            ControllerClientCount: 3));

        Assert.AreEqual(2, assignment.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.Auto, assignment.InputMode);
        Assert.IsFalse(assignment.ControllerDevice.IsConfigured);
        Assert.IsNull(assignment.ControllerDevice.Device);
        Assert.AreEqual(3, assignment.ControllerClientCount);
    }

    [TestMethod]
    public void ResolvesNoneAssignmentWithoutControllerDevice()
    {
        var assignment = ControllerAssignmentService.Resolve(new BrokerClientConfig(
            BrokerClientRole.Client,
            ClientIndex: 1,
            Host: "127.0.0.1",
            Port: 38989,
            SessionId: "local-test",
            PlayerSlot: 1,
            InputMode: BrokerClientInputMode.None));

        Assert.AreEqual(1, assignment.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.None, assignment.InputMode);
        Assert.IsTrue(assignment.ControllerDevice.IsConfigured);
        Assert.IsNull(assignment.ControllerDevice.Device);
    }
}

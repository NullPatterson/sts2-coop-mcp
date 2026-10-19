using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Protocol.Tests;

[TestClass]
public sealed class BrokerClientConfigTests
{
    [TestMethod]
    public void ParsesHostConfig()
    {
        var config = BrokerClientConfig.Parse("""
            role=host
            clientIndex=0
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(BrokerClientRole.Host, config.Role);
        Assert.AreEqual(0, config.ClientIndex);
        Assert.AreEqual("127.0.0.1", config.Host);
        Assert.AreEqual(38989, config.Port);
        Assert.AreEqual("local-test", config.SessionId);
    }

    [TestMethod]
    public void RejectsClientIndexOutsideFourLocalClients()
    {
        var exception = Assert.ThrowsException<FormatException>(() => BrokerClientConfig.Parse("""
            role=client
            clientIndex=4
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """));

        StringAssert.Contains(exception.Message, "clientIndex");
    }

    [TestMethod]
    public void ParsesClientConfigAtClientIndexZero()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=0
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(BrokerClientRole.Client, config.Role);
        Assert.AreEqual(0, config.ClientIndex);
    }

    [TestMethod]
    public void ParsesHostConfigAtNonzeroClientIndex()
    {
        var config = BrokerClientConfig.Parse("""
            role=host
            clientIndex=2
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(BrokerClientRole.Host, config.Role);
        Assert.AreEqual(2, config.ClientIndex);
    }

    [TestMethod]
    public void ParsesAssignedControllerDevice()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            controllerDevice=1
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.IsFalse(config.ControllerDevice.IsConfigured);
        Assert.IsNull(config.ControllerDevice.Device);
    }

    [TestMethod]
    public void ParsesCanonicalPlayerSlotAndInputMode()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=2
            playerSlot=3
            inputMode=auto
            controllerClientCount=3
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(3, config.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.Auto, config.InputMode);
        Assert.AreEqual(3, config.ControllerClientCount);
        Assert.IsFalse(config.ControllerDevice.IsConfigured);
        Assert.IsNull(config.ControllerDevice.Device);
    }

    [TestMethod]
    public void RejectsControllerClientCountOutsideFourLocalClients()
    {
        var exception = Assert.ThrowsException<FormatException>(() => BrokerClientConfig.Parse("""
            role=client
            clientIndex=2
            playerSlot=2
            inputMode=auto
            controllerClientCount=5
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """));

        StringAssert.Contains(exception.Message, "controllerClientCount");
    }

    [TestMethod]
    public void LegacyControllerDeviceIntegerMapsToPlayerSlot()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            controllerDevice=2
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(2, config.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.Auto, config.InputMode);
    }

    [TestMethod]
    public void LegacyControllerDeviceNoneMapsToInputModeNone()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            controllerDevice=none
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(1, config.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.None, config.InputMode);
        Assert.IsTrue(config.ControllerDevice.IsConfigured);
        Assert.IsNull(config.ControllerDevice.Device);
    }

    [TestMethod]
    public void ParsesDisabledControllerDevice()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            controllerDevice=none
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.IsTrue(config.ControllerDevice.IsConfigured);
        Assert.IsNull(config.ControllerDevice.Device);
    }

    [TestMethod]
    public void DefaultsControllerDeviceToUnconfiguredWhenKeyIsAbsent()
    {
        var config = BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        Assert.AreEqual(1, config.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.Auto, config.InputMode);
        Assert.IsFalse(config.ControllerDevice.IsConfigured);
        Assert.IsNull(config.ControllerDevice.Device);
    }

    [TestMethod]
    public void RejectsControllerDeviceOutsideFourLocalClients()
    {
        var exception = Assert.ThrowsException<FormatException>(() => BrokerClientConfig.Parse("""
            role=client
            clientIndex=1
            controllerDevice=4
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """));

        StringAssert.Contains(exception.Message, "controllerDevice");
    }
}

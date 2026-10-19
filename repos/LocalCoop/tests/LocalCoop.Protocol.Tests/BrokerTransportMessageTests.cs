using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Protocol.Tests;

[TestClass]
public sealed class BrokerTransportMessageTests
{
    [TestMethod]
    public void CreatesRegistrationAcceptedMessage()
    {
        var accepted = BrokerTransportMessage.ForRegistrationAccepted("client-1", "local-test");

        Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, accepted.Kind);
        Assert.AreEqual("client-1", accepted.RegistrationAccepted?.ClientId);
        Assert.AreEqual("local-test", accepted.RegistrationAccepted?.SessionId);
    }

    [TestMethod]
    public void CreatesPeerRegisteredMessage()
    {
        var message = BrokerTransportMessage.ForPeerRegistered(
            new BrokerClientRegistrationDto("client-1", BrokerClientRole.Client, 1));

        Assert.AreEqual(BrokerTransportMessageKind.PeerRegistered, message.Kind);
        Assert.AreEqual("client-1", message.PeerRegistration?.ClientId);
        Assert.AreEqual(BrokerClientRole.Client, message.PeerRegistration?.Role);
        Assert.AreEqual(1, message.PeerRegistration?.ClientIndex);
    }

    [TestMethod]
    public void CreatesRegistrationRejectedMessage()
    {
        var message = BrokerTransportMessage.ForRegistrationRejected("version mismatch");

        Assert.AreEqual(BrokerTransportMessageKind.RegistrationRejected, message.Kind);
        Assert.AreEqual("version mismatch", message.RegistrationRejected?.Reason);
    }

    [TestMethod]
    public void TransportKindsCoverRegistrationEnvelopeAndPeerControl()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                BrokerTransportMessageKind.Registration,
                BrokerTransportMessageKind.RegistrationAccepted,
                BrokerTransportMessageKind.RegistrationRejected,
                BrokerTransportMessageKind.Envelope,
                BrokerTransportMessageKind.PeerRegistered
            },
            Enum.GetValues<BrokerTransportMessageKind>());
    }

    [TestMethod]
    public void ControllerStatusRoundTripsWarmStandbyReadiness()
    {
        var status = new CollectorStatusMessage(
            ClientIndex: 2,
            WindowFocused: true,
            SteamReady: true,
            LogoReady: true,
            ControllerEnabled: true,
            ControllerClientCount: 4,
            ActionDataReady: true,
            ConnectedControllerCount: 4,
            ActiveDigitalActionCount: 60,
            DigitalActionQueryCount: 60,
            ActiveAnalogActionCount: 4,
            AnalogActionQueryCount: 4);
        var envelope = new BrokerEnvelope(
            "local-test",
            "client-2",
            null,
            ControllerControlMessageTypes.CollectorStatus,
            ControllerControlMessageSerializer.Serialize(status),
            1);

        var parsed = ControllerControlMessageSerializer.Deserialize<CollectorStatusMessage>(envelope);

        Assert.AreEqual(status, parsed);
        Assert.IsTrue(parsed.ActionDataReady);
        Assert.AreEqual(60, parsed.ActiveDigitalActionCount);
    }
}

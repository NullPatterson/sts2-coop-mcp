using LocalCoop.Broker;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace LocalCoop.Protocol.Tests;

[TestClass]
public sealed class BrokerClientConnectionTests
{
    [TestMethod]
    public async Task ConnectsRegistersAndReceivesRoutedEnvelope()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);

        await using var host = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", server.Port, "local-test"),
            clientId: "host",
            CancellationToken.None);
        await using var client = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Client, 1, "127.0.0.1", server.Port, "local-test"),
            clientId: "client-1",
            CancellationToken.None);

        await host.SendEnvelopeAsync(BrokerEnvelope.Broadcast(
            "local-test",
            "host",
            "LobbyChanged",
            [10, 11],
            sequence: 1), CancellationToken.None);

        var received = await client.ReadEnvelopeAsync(CancellationToken.None);

        Assert.IsNotNull(received);
        Assert.AreEqual("LobbyChanged", received.MessageType);
        CollectionAssert.AreEqual(new byte[] { 10, 11 }, received.Payload);
    }

    [TestMethod]
    public async Task Sts2EnvelopeArrivesWithoutClientStatus()
    {
        const string messageType = "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.LobbyPlayerSetReadyMessage, sts2";
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);

        await using var host = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", server.Port, "local-test"),
            clientId: "host",
            CancellationToken.None);
        await using var client = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Client, 1, "127.0.0.1", server.Port, "local-test"),
            clientId: "client-1",
            CancellationToken.None);

        await host.SendEnvelopeAsync(BrokerEnvelope.Broadcast(
            "local-test",
            "host",
            messageType,
            [12],
            sequence: 2), CancellationToken.None);

        var received = await client.ReadEnvelopeAsync(CancellationToken.None);

        Assert.IsNotNull(received);
        Assert.AreEqual(messageType, received.MessageType);
        CollectionAssert.AreEqual(new byte[] { 12 }, received.Payload);
    }
}

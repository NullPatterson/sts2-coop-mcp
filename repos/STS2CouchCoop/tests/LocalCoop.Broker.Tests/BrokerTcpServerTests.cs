using LocalCoop.Broker;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;

namespace LocalCoop.Broker.Tests;

[TestClass]
public sealed class BrokerTcpServerTests
{
    private const string LobbyMessageType = "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.LobbyPlayerSetReadyMessage, sts2";
    private const string ChoiceMessageType = "MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.OptionIndexChosenMessage, sts2";
    private const string CombatMessageType = "MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums.StateDivergenceMessage, sts2";

    [TestMethod]
    public async Task RoutesEnvelopeBetweenRegisteredTcpClients()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);

        using var host = new TcpClient();
        using var client = new TcpClient();
        await host.ConnectAsync(IPAddress.Loopback, server.Port);
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto("host", BrokerClientRole.Host, 0)),
            CancellationToken.None);
        var hostAccepted = await BrokerFrameCodec.ReadAsync(host.GetStream(), CancellationToken.None);
        Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, hostAccepted?.Kind);
        Assert.AreEqual("host", hostAccepted?.RegistrationAccepted?.ClientId);

        await BrokerFrameCodec.WriteAsync(
            client.GetStream(),
            BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto("client-1", BrokerClientRole.Client, 1)),
            CancellationToken.None);
        var clientAccepted = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);
        Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, clientAccepted?.Kind);
        Assert.AreEqual("client-1", clientAccepted?.RegistrationAccepted?.ClientId);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(BrokerEnvelope.Broadcast(
                "local-test",
                "host",
                "LobbyChanged",
                [8, 9],
                sequence: 1)),
            CancellationToken.None);

        var routed = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);

        Assert.IsNotNull(routed);
        Assert.AreEqual(BrokerTransportMessageKind.Envelope, routed.Kind);
        Assert.AreEqual("host", routed.Envelope?.SourceClientId);
        Assert.AreEqual("LobbyChanged", routed.Envelope?.MessageType);
        CollectionAssert.AreEqual(new byte[] { 8, 9 }, routed.Envelope?.Payload.ToArray());
    }

    [TestMethod]
    public async Task RejectsIncompatibleProtocolVersionWithClearReason()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        await BrokerFrameCodec.WriteAsync(
            client.GetStream(),
            BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto(
                "client-0",
                BrokerClientRole.Host,
                0,
                ProtocolVersion: BrokerProtocol.CurrentVersion - 1)),
            CancellationToken.None);
        var rejected = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);

        Assert.AreEqual(BrokerTransportMessageKind.RegistrationRejected, rejected?.Kind);
        StringAssert.Contains(rejected?.RegistrationRejected?.Reason, "expected");
        StringAssert.Contains(rejected?.RegistrationRejected?.Reason, BrokerProtocol.CurrentVersion.ToString());
    }

    [TestMethod]
    public async Task AllowsClientIndexToReconnectAfterDisconnect()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);

        using (var firstHost = new TcpClient())
        {
            await firstHost.ConnectAsync(IPAddress.Loopback, server.Port);
            await BrokerFrameCodec.WriteAsync(
                firstHost.GetStream(),
                BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto("client-0", BrokerClientRole.Host, 0)),
                CancellationToken.None);
            var firstAccepted = await BrokerFrameCodec.ReadAsync(firstHost.GetStream(), CancellationToken.None);
            Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, firstAccepted?.Kind);
        }

        await WaitForAsync(async () =>
        {
            using var secondHost = new TcpClient();
            await secondHost.ConnectAsync(IPAddress.Loopback, server.Port);
            await BrokerFrameCodec.WriteAsync(
                secondHost.GetStream(),
                BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto("client-0", BrokerClientRole.Host, 0)),
                CancellationToken.None);
            var secondAccepted = await BrokerFrameCodec.ReadAsync(secondHost.GetStream(), CancellationToken.None);
            return secondAccepted?.Kind == BrokerTransportMessageKind.RegistrationAccepted;
        });
    }

    [TestMethod]
    public async Task QueuesClientEnvelopeUntilHostRegisters()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);

        await BrokerFrameCodec.WriteAsync(
            client.GetStream(),
            BrokerTransportMessage.ForEnvelope(BrokerEnvelope.Broadcast(
                "local-test",
                "client-1",
                "ClientLobbyJoinRequest",
                [1],
                sequence: 1)),
            CancellationToken.None);

        using var host = await ConnectRegisteredClientAsync(server.Port, "host", BrokerClientRole.Host, 0);

        var routed = await BrokerFrameCodec.ReadAsync(host.GetStream(), CancellationToken.None);

        Assert.AreEqual(BrokerTransportMessageKind.Envelope, routed?.Kind);
        Assert.AreEqual("ClientLobbyJoinRequest", routed?.Envelope?.MessageType);
        Assert.AreEqual(1, routed?.Envelope?.Sequence);
    }

    [TestMethod]
    public async Task RegistrationAcceptedIncludesAlreadyRegisteredPeers()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);
        using var host = new TcpClient();
        await host.ConnectAsync(IPAddress.Loopback, server.Port);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto("client-0", BrokerClientRole.Host, 0)),
            CancellationToken.None);

        var accepted = await BrokerFrameCodec.ReadAsync(host.GetStream(), CancellationToken.None);

        Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, accepted?.Kind);
        var peer = accepted?.RegistrationAccepted?.ConnectedPeers.SingleOrDefault();
        Assert.IsNotNull(peer);
        Assert.AreEqual("client-1", peer!.ClientId);
        Assert.AreEqual(BrokerClientRole.Client, peer.Role);
        Assert.AreEqual(1, peer.ClientIndex);
    }

    [TestMethod]
    public async Task AlreadyRegisteredClientReceivesPeerRegisteredWhenNewPeerRegisters()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var host = await ConnectRegisteredClientAsync(server.Port, "client-0", BrokerClientRole.Host, 0);

        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);

        var peerRegistered = await BrokerFrameCodec.ReadAsync(host.GetStream(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(BrokerTransportMessageKind.PeerRegistered, peerRegistered?.Kind);
        Assert.AreEqual("client-1", peerRegistered?.PeerRegistration?.ClientId);
        Assert.AreEqual(BrokerClientRole.Client, peerRegistered?.PeerRegistration?.Role);
        Assert.AreEqual(1, peerRegistered?.PeerRegistration?.ClientIndex);
    }

    [TestMethod]
    public async Task QueuesDirectEnvelopeUntilTargetRegisters()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var host = await ConnectRegisteredClientAsync(server.Port, "host", BrokerClientRole.Host, 0);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(new BrokerEnvelope(
                "local-test",
                "host",
                TargetClientId: "client-1",
                MessageType: ChoiceMessageType,
                Payload: [2],
                Sequence: 2)),
            CancellationToken.None);

        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);

        var routed = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);

        Assert.AreEqual(BrokerTransportMessageKind.Envelope, routed?.Kind);
        Assert.AreEqual(ChoiceMessageType, routed?.Envelope?.MessageType);
        Assert.AreEqual(2, routed?.Envelope?.Sequence);
    }

    [TestMethod]
    public async Task Sts2MessageTypesReleaseImmediatelyWhenTargetConnected()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var host = await ConnectRegisteredClientAsync(server.Port, "host", BrokerClientRole.Host, 0);
        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(BrokerEnvelope.Broadcast(
                "local-test",
                "host",
                ChoiceMessageType,
                [2],
                sequence: 2)),
            CancellationToken.None);
        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(BrokerEnvelope.Broadcast(
                "local-test",
                "host",
                CombatMessageType,
                [3],
                sequence: 3)),
            CancellationToken.None);

        var first = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);
        var second = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);

        Assert.AreEqual(ChoiceMessageType, first?.Envelope?.MessageType);
        Assert.AreEqual(CombatMessageType, second?.Envelope?.MessageType);
    }

    [TestMethod]
    public async Task QueuedEnvelopesReleaseInFifoOrderWhenTargetRegisters()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        using var host = await ConnectRegisteredClientAsync(server.Port, "host", BrokerClientRole.Host, 0);

        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(new BrokerEnvelope(
                "local-test",
                "host",
                TargetClientId: "client-1",
                MessageType: ChoiceMessageType,
                Payload: [4],
                Sequence: 4)),
            CancellationToken.None);
        await BrokerFrameCodec.WriteAsync(
            host.GetStream(),
            BrokerTransportMessage.ForEnvelope(new BrokerEnvelope(
                "local-test",
                "host",
                TargetClientId: "client-1",
                MessageType: ChoiceMessageType,
                Payload: [5],
                Sequence: 5)),
            CancellationToken.None);

        using var client = await ConnectRegisteredClientAsync(server.Port, "client-1", BrokerClientRole.Client, 1);

        var first = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);
        var second = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);

        Assert.AreEqual(4, first?.Envelope?.Sequence);
        Assert.AreEqual(5, second?.Envelope?.Sequence);
        CollectionAssert.AreEqual(new byte[] { 4 }, first?.Envelope?.Payload.ToArray());
        CollectionAssert.AreEqual(new byte[] { 5 }, second?.Envelope?.Payload.ToArray());
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Condition was not satisfied before timeout.");
    }

    private static async Task<TcpClient> ConnectRegisteredClientAsync(
        int port,
        string clientId,
        BrokerClientRole role,
        int clientIndex)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await BrokerFrameCodec.WriteAsync(
            client.GetStream(),
            BrokerTransportMessage.ForRegistration(new BrokerClientRegistrationDto(clientId, role, clientIndex)),
            CancellationToken.None);

        var accepted = await BrokerFrameCodec.ReadAsync(client.GetStream(), CancellationToken.None);
        Assert.AreEqual(BrokerTransportMessageKind.RegistrationAccepted, accepted?.Kind);
        return client;
    }
}

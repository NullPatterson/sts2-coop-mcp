using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Channels;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerNetServiceFactoryTests
{
    [TestMethod]
    public void TryCreateReturnsNullWhenBrokerModeIsDisabled()
    {
        var settings = new BrokerModeSettings(false, null, "client-0", "events.txt", "disabled");

        var service = BrokerNetServiceFactory.TryCreate(settings, new CapturingTransport());

        Assert.IsNull(service);
    }

    [TestMethod]
    public void TryCreateBuildsServiceFromEnabledBrokerSettings()
    {
        var settings = new BrokerModeSettings(
            true,
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", 38989, "local-test"),
            "client-0",
            "events.txt",
            null);

        var service = BrokerNetServiceFactory.TryCreate(settings, new CapturingTransport());

        Assert.IsNotNull(service);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), service.NetId);
        Assert.AreEqual("local-test", service.GetRawLobbyIdentifier());
    }

    [TestMethod]
    public void TryCreatePassesLoggerToBrokerService()
    {
        var settings = new BrokerModeSettings(
            true,
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", 38989, "local-test"),
            "client-0",
            "events.txt",
            null);
        var logs = new List<string>();

        var service = BrokerNetServiceFactory.TryCreate(settings, new CapturingTransport(), logs.Add);
        service!.SendMessage(new FakeLobbyMessage("ready"));

        StringAssert.Contains(logs.Single(), "Broker outbound");
    }


    [TestMethod]
    public void BrokerNetGameServiceImplementsHostGameServiceForHostLobby()
    {
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            new CapturingTransport());
        var service = new BrokerNetGameService(inner, NetGameType.Host);

        Assert.IsInstanceOfType<INetHostGameService>(service);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceStartsReceiveLoop()
    {
        var transport = new QueuedTransport();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-1",
            1,
            transport);
        var receivedSource = new TaskCompletionSource<FakeLobbyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.RegisterMessageHandler<FakeLobbyMessage>(message => receivedSource.SetResult(message));
        using var service = new BrokerNetGameService(inner, NetGameType.Client);

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("ready"),
            sequence: 1));

        await Task.Delay(50);
        service.Update();
        var received = await receivedSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Disconnect(default, now: true);

        Assert.AreEqual("ready", received.Kind);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceUpdateDispatchesQueuedHandlerMessages()
    {
        var transport = new QueuedTransport();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-1",
            1,
            transport);
        var receivedCount = 0;
        inner.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(_ => receivedCount++);
        using var service = new BrokerNetGameService(inner, NetGameType.Client);

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage(),
            sequence: 1));

        await Task.Delay(50);
        Assert.AreEqual(0, receivedCount);

        service.Update();
        service.Disconnect(default, now: true);

        Assert.AreEqual(1, receivedCount);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceDispatchRestoresPreviousLocalContextAfterNativeHandlers()
    {
        var previousNetId = LocalContext.NetId;
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            new CapturingTransport());
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        ulong? observedNetId = null;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>((_, _) => observedNetId = LocalContext.NetId);

        try
        {
            LocalContext.NetId = BrokerPlayerId.ForClientIndex(1);

            await inner.DispatchEnvelopeAsync(
                BrokerEnvelopeMessageSerializer.ToEnvelope(
                    "local-test",
                    "client-1",
                    targetClientId: "client-0",
                    new LobbyPlayerSetReadyMessage(),
                    sequence: 1),
                CancellationToken.None);

            Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), observedNetId);
            Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), LocalContext.NetId);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public async Task BrokerNetGameServiceMarksNativeHandlerDispatch()
    {
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            new CapturingTransport());
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        bool? observedDispatch = null;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>((_, _) =>
            observedDispatch = BrokerNetGameService.IsDispatchingNativeMessageHandlerForTesting);

        await inner.DispatchEnvelopeAsync(
            BrokerEnvelopeMessageSerializer.ToEnvelope(
                "local-test",
                "client-1",
                targetClientId: "client-0",
                new LobbyPlayerSetReadyMessage(),
                sequence: 1),
            CancellationToken.None);

        Assert.AreEqual(true, observedDispatch);
        Assert.IsFalse(BrokerNetGameService.IsDispatchingNativeMessageHandlerForTesting);
    }

    [TestMethod]
    public void BrokerNetGameServiceConnectedPeersDoesNotAssumeHostClientIndex()
    {
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-1",
            1,
            new CapturingTransport());
        using var service = new BrokerNetGameService(inner, NetGameType.Client);

        Assert.AreEqual(0, service.ConnectedPeers.Count);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceUpdateFiresClientConnectedFromJoinRequest()
    {
        var transport = new QueuedTransport();
        var logs = new List<string>();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            transport,
            logs.Add);
        inner.RegisterMessageHandler<ClientLobbyJoinRequestMessage>(_ => { });
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        ulong? connectedPeer = null;
        service.ClientConnected += peerId => connectedPeer = peerId;

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-1",
            targetClientId: "client-0",
            BrokerClientJoinFlow.CreateJoinRequest(0, new MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState()),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        service.Disconnect(default, now: true);

        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), connectedPeer, string.Join(Environment.NewLine, logs));
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), service.ConnectedPeers.Single().peerId);
    }

    [TestMethod]
    public void BrokerNetGameServiceFirstUpdateFiresClientConnectedForInitialBrokerPeer()
    {
        var transport = new CapturingTransport(
            [new BrokerClientRegistrationInfo("client-1", BrokerClientRole.Client, 1)]);
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            transport);
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        ulong? connectedPeer = null;
        service.ClientConnected += peerId => connectedPeer = peerId;

        service.Update();

        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), connectedPeer);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), service.ConnectedPeers.Single().peerId);
    }

    [TestMethod]
    public void BrokerNetGameServiceUpdateFiresClientConnectedForLaterBrokerPeer()
    {
        var transport = new CapturingTransport();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            transport);
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        ulong? connectedPeer = null;
        service.ClientConnected += peerId => connectedPeer = peerId;

        transport.NotifyPeerRegistered(new BrokerClientRegistrationInfo("client-1", BrokerClientRole.Client, 1));
        service.Update();

        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), connectedPeer);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), service.ConnectedPeers.Single().peerId);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceFiresClientConnectedBeforeJoinRequestHandler()
    {
        var transport = new QueuedTransport();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            transport);
        var order = new List<string>();
        inner.RegisterMessageHandler<ClientLobbyJoinRequestMessage>(_ => order.Add("handler"));
        using var service = new BrokerNetGameService(inner, NetGameType.Host);
        service.ClientConnected += _ => order.Add("connected");

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-1",
            targetClientId: "client-0",
            BrokerClientJoinFlow.CreateJoinRequest(0, new MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState()),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        service.Disconnect(default, now: true);

        CollectionAssert.AreEqual(new[] { "connected", "handler" }, order);
    }

    [TestMethod]
    public async Task BrokerNetGameServiceSetPeerReadyForBroadcastingUpdatesConnectedPeer()
    {
        var transport = new QueuedTransport();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            transport);
        inner.RegisterMessageHandler<ClientLobbyJoinRequestMessage>(_ => { });
        using var service = new BrokerNetGameService(inner, NetGameType.Host);

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-1",
            targetClientId: "client-0",
            BrokerClientJoinFlow.CreateJoinRequest(0, new MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState()),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        service.SetPeerReadyForBroadcasting(BrokerPlayerId.ForClientIndex(1));
        service.Disconnect(default, now: true);

        var peer = service.ConnectedPeers.Single();
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), peer.peerId);
        Assert.IsTrue(peer.readyForBroadcasting);
    }

    [TestMethod]
    public void BrokerNetGameServiceAcceptsNativeMessageBufferToggle()
    {
        var logs = new List<string>();
        var inner = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            new CapturingTransport(),
            logs.Add);
        using var service = new BrokerNetGameService(inner, NetGameType.Host);

        service.SetBufferMessages(true);
        service.SetBufferMessages(false);

        Assert.IsFalse(inner.IsBufferingMessages);
        Assert.IsTrue(logs.Any(message => message.Contains("bufferMessages=True", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(message => message.Contains("bufferMessages=False", StringComparison.Ordinal)));
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public CapturingTransport(IReadOnlyList<BrokerClientRegistrationInfo>? connectedPeers = null)
        {
            ConnectedPeers = connectedPeers ?? [];
        }

        public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers { get; }

        public event Action<BrokerClientRegistrationInfo>? PeerRegistered;

        public void NotifyPeerRegistered(BrokerClientRegistrationInfo registration)
        {
            PeerRegistered?.Invoke(registration);
        }

        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<BrokerEnvelope?>(null);
        }
    }

    private readonly record struct FakeLobbyMessage(string Kind);

    private sealed class QueuedTransport : IBrokerEnvelopeTransport
    {
        private readonly Channel<BrokerEnvelope?> _incoming = Channel.CreateUnbounded<BrokerEnvelope?>();

        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }

        public async Task QueueEnvelopeAsync(BrokerEnvelope envelope)
        {
            await _incoming.Writer.WriteAsync(envelope);
        }
    }
}

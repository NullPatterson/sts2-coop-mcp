using LocalCoop.Mod.Runtime;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LocalCoop.Broker;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using System.Net;
using System.Threading.Channels;
using Runtime = LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerBackedNetServiceTests
{
    [TestMethod]
    public void NetIdIsStableForClientIndex()
    {
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), BrokerPlayerId.ForClientIndex(0));
        Assert.AreNotEqual(BrokerPlayerId.ForClientIndex(0), BrokerPlayerId.ForClientIndex(1));
    }

    [TestMethod]
    public async Task SendMessageCreatesTypedEnvelope()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport);

        await service.SendMessageAsync(new FakeLobbyMessage("ready"), targetPlayerId: BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        var envelope = transport.Sent.Single();
        Assert.AreEqual("local-test", envelope.SessionId);
        Assert.AreEqual("client-0", envelope.SourceClientId);
        Assert.AreEqual("client-1", envelope.TargetClientId);
        Assert.AreEqual(typeof(FakeLobbyMessage).AssemblyQualifiedName, envelope.MessageType);
        CollectionAssert.Contains(envelope.Payload, (byte)'r');
    }

    [TestMethod]
    public async Task SendMessageLogsOutboundEnvelope()
    {
        var transport = new CapturingTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport,
            logs.Add);

        await service.SendMessageAsync(new FakeLobbyMessage("ready"), targetPlayerId: BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        var outboundLog = logs.Single(log => log.Contains("Broker outbound", StringComparison.Ordinal));
        StringAssert.Contains(outboundLog, "local-test");
        StringAssert.Contains(outboundLog, "client-0");
        StringAssert.Contains(outboundLog, "client-1");
        StringAssert.Contains(outboundLog, nameof(FakeLobbyMessage));
    }

    [TestMethod]
    public void SyncSendMessageBroadcastCreatesTypedEnvelope()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport);

        service.SendMessage(new FakeLobbyMessage("appearance"));

        var envelope = transport.Sent.Single();
        Assert.AreEqual("client-0", envelope.SourceClientId);
        Assert.IsNull(envelope.TargetClientId);
        Assert.AreEqual(typeof(FakeLobbyMessage).AssemblyQualifiedName, envelope.MessageType);
    }

    [TestMethod]
    public void SendMessagePreservesPeerInputMessages()
    {
        var transport = new CapturingTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-1",
            clientIndex: 1,
            transport,
            logs.Add);

        service.SendMessage(new PeerInputMessage());

        var envelope = transport.Sent.Single();
        Assert.AreEqual(typeof(PeerInputMessage).AssemblyQualifiedName, envelope.MessageType);
        Assert.IsFalse(logs.Any(log => log.Contains("suppressed outbound", StringComparison.Ordinal)
            && log.Contains(nameof(PeerInputMessage), StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SendJoinResponseDoesNotReplayCachedLobbyCharacterStateToJoiningClient()
    {
        var transport = new CapturingTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport,
            logs.Add);
        await service.DispatchEnvelopeAsync(EnvelopeFor<LobbyPlayerChangedCharacterMessage>("client-0", targetClientId: null, sequence: 7), CancellationToken.None);
        var joinResponse = new ClientLobbyJoinResponseMessage
        {
            playersInLobby = [],
            modifiers = []
        };

        await service.SendMessageAsync(joinResponse, BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        Assert.AreEqual(1, transport.Sent.Count);
        Assert.AreEqual(typeof(ClientLobbyJoinResponseMessage).AssemblyQualifiedName, transport.Sent.Single().MessageType);
        Assert.IsFalse(logs.Any(log => log.Contains("Broker replay outbound", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SendJoinResponseCompletesWithoutReplayTimer()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport);
        await service.DispatchEnvelopeAsync(EnvelopeFor<LobbyPlayerChangedCharacterMessage>("client-0", targetClientId: null, sequence: 7), CancellationToken.None);
        var joinResponse = new ClientLobbyJoinResponseMessage
        {
            playersInLobby = [],
            modifiers = []
        };

        var sendTask = service.SendMessageAsync(joinResponse, BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        Assert.IsTrue(sendTask.IsCompleted);
        await sendTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, transport.Sent.Count);
        Assert.AreEqual(typeof(ClientLobbyJoinResponseMessage).AssemblyQualifiedName, transport.Sent[0].MessageType);
    }

    [TestMethod]
    public async Task SendJoinResponseDoesNotReplayAnyCachedCharacterState()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            transport);
        await service.DispatchEnvelopeAsync(EnvelopeFor<LobbyPlayerChangedCharacterMessage>("client-1", targetClientId: null, sequence: 7), CancellationToken.None);
        var joinResponse = new ClientLobbyJoinResponseMessage
        {
            playersInLobby = [],
            modifiers = []
        };

        await service.SendMessageAsync(joinResponse, BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        Assert.AreEqual(1, transport.Sent.Count);
        Assert.AreEqual(typeof(ClientLobbyJoinResponseMessage).AssemblyQualifiedName, transport.Sent[0].MessageType);
    }

    [TestMethod]
    public void TracksConnectionLoadingAndRawLobbyIdentifier()
    {
        var log = new List<string>();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            new CapturingTransport(),
            log.Add);

        Assert.IsTrue(service.IsConnected);
        Assert.IsFalse(service.IsGameLoading);

        service.SetGameLoading(true);

        Assert.IsTrue(service.IsGameLoading);
        Assert.AreEqual("local-test", service.GetRawLobbyIdentifier());
        Assert.IsTrue(log.Any(line => line.Contains("Broker game loading changed", StringComparison.Ordinal)
            && line.Contains("isGameLoading=True", StringComparison.Ordinal)));
        service.Update();
    }

    [TestMethod]
    public async Task DispatchEnvelopeInvokesRegisteredHandler()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        FakeLobbyMessage? received = null;
        service.RegisterMessageHandler<FakeLobbyMessage>(message => received = message);

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("pick-ironclad"),
            sequence: 1),
            CancellationToken.None);

        Assert.IsNotNull(received);
        Assert.AreEqual("pick-ironclad", received.Value.Kind);
    }

    [TestMethod]
    public async Task DispatchEnvelopePreservesPublicFieldMessages()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        FieldLobbyMessage received = default;
        service.RegisterMessageHandler<FieldLobbyMessage>(message => received = message);

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FieldLobbyMessage { kind = "ready" },
            sequence: 1),
            CancellationToken.None);

        Assert.AreEqual("ready", received.kind);
    }

    [TestMethod]
    public async Task DispatchEnvelopeUsesPacketSerializationForNetMessages()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        PacketSerializableMessage received = default;
        service.RegisterMessageHandler<PacketSerializableMessage>(message => received = message);

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new PacketSerializableMessage { value = "ready", unsupported = new IntPtr(42) },
            sequence: 1),
            CancellationToken.None);

        Assert.AreEqual("ready", received.value);
    }

    [TestMethod]
    public async Task DispatchEnvelopeLogsInboundEnvelope()
    {
        var transport = new CapturingTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport, logs.Add);

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("ready"),
            sequence: 1),
            CancellationToken.None);

        var inboundLog = logs.Single(log => log.Contains("Broker inbound", StringComparison.Ordinal));
        StringAssert.Contains(inboundLog, "local-test");
        StringAssert.Contains(inboundLog, "client-0");
        StringAssert.Contains(inboundLog, "client-1");
        StringAssert.Contains(inboundLog, nameof(FakeLobbyMessage));
    }

    [TestMethod]
    public async Task DispatchEnvelopeInvokesRegisteredHandlerWithSenderId()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        FakeLobbyMessage? received = null;
        ulong? sender = null;
        service.RegisterMessageHandler<FakeLobbyMessage>((message, senderId) =>
        {
            received = message;
            sender = senderId;
        });

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("ready"),
            sequence: 1),
            CancellationToken.None);

        Assert.IsNotNull(received);
        Assert.AreEqual("ready", received.Value.Kind);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), sender);
    }

    [TestMethod]
    public async Task UnregisterMessageHandlerStopsDispatch()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var count = 0;
        Action<FakeLobbyMessage> handler = _ => count++;
        service.RegisterMessageHandler(handler);
        service.UnregisterMessageHandler(handler);

        await service.DispatchEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("ready"),
            sequence: 1),
            CancellationToken.None);

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task ReceiveLoopDispatchesInboundEnvelopes()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var receivedSource = new TaskCompletionSource<FakeLobbyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RegisterMessageHandler<FakeLobbyMessage>(message => receivedSource.SetResult(message));

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("appearance"),
            sequence: 1));

        await Task.Delay(50);
        service.Update();
        var received = await receivedSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual("appearance", received.Kind);
    }

    [TestMethod]
    public async Task ReceiveLoopQueuesInboundEnvelopesUntilUpdate()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        FakeLobbyMessage? received = null;
        service.RegisterMessageHandler<FakeLobbyMessage>(message => received = message);

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("queued"),
            sequence: 1));
        await Task.Delay(50);

        Assert.IsNull(received);

        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsNotNull(received);
        Assert.AreEqual("queued", received.Value.Kind);
    }

    [TestMethod]
    public async Task ReceiveLoopKeepsBufferableInboundEnvelopesQueuedWhileBufferingMessages()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        bool? received = null;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(message => received = message.ready);

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 1));
        await Task.Delay(50);

        service.SetBufferMessages(true);
        service.Update();

        Assert.IsNull(received);

        service.SetBufferMessages(false);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsNotNull(received);
        Assert.IsTrue(received.Value);
    }

    [TestMethod]
    public async Task ReceiveLoopHandlersRunOnUpdateThread()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        int? handlerThreadId = null;
        service.RegisterMessageHandler<FakeLobbyMessage>(_ => handlerThreadId = Environment.CurrentManagedThreadId);

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("thread"),
            sequence: 1));
        await Task.Delay(50);

        var updateThreadId = Environment.CurrentManagedThreadId;
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(updateThreadId, handlerThreadId);
    }

    [TestMethod]
    public async Task UpdateLogsFlushedAndDispatchedInboundEnvelope()
    {
        var transport = new QueuedTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport, logs.Add);
        service.RegisterMessageHandler<FakeLobbyMessage>(_ => { });

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("logs"),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(logs.Any(log => log.Contains("Broker inbound flushed", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(log => log.Contains("Broker inbound dispatched", StringComparison.Ordinal)
            && log.Contains("handlerCount=1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RegisterMessageHandlerLogsMessageTypeAndCount()
    {
        var transport = new CapturingTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport, logs.Add);

        service.RegisterMessageHandler<FakeLobbyMessage>(_ => { });

        Assert.IsTrue(logs.Any(log => log.Contains("Broker handler registered", StringComparison.Ordinal)
            && log.Contains(typeof(FakeLobbyMessage).AssemblyQualifiedName!, StringComparison.Ordinal)
            && log.Contains("handlerCount=1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task InboundEnvelopeWaitsForExactHandlerRegistration()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var envelope = EnvelopeForMessage(
            "client-0",
            targetClientId: null,
            new LobbyPlayerSetReadyMessage(),
            sequence: 1);

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(envelope);
        await Task.Delay(50);
        service.Update();

        var receivedCount = 0;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(_ => receivedCount++);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, receivedCount);
    }

    [TestMethod]
    public async Task DuplicateInboundMessagesDispatchInFifoOrder()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var envelope = EnvelopeForMessage(
            "client-0",
            targetClientId: null,
            new LobbyPlayerSetReadyMessage(),
            sequence: 1);
        var receivedCount = 0;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>((_, _) => receivedCount++);

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(envelope);
        await transport.QueueEnvelopeAsync(envelope with { Sequence = 2 });
        await Task.Delay(50);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, receivedCount);
    }

    [TestMethod]
    public async Task BufferableInboundMessagesWaitUntilBufferingIsDisabled()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var received = new List<bool>();
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(message => received.Add(message.ready));

        service.SetBufferMessages(true);
        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 1));
        await Task.Delay(50);
        service.Update();

        Assert.AreEqual(0, received.Count);

        service.SetBufferMessages(false);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { true }, received);
    }

    [TestMethod]
    public async Task BufferedMessagesFlushInFifoOrder()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var received = new List<bool>();
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(message => received.Add(message.ready));

        service.SetBufferMessages(true);
        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage { ready = false },
            sequence: 1));
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 2));
        await Task.Delay(50);
        service.Update();

        service.SetBufferMessages(false);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { false, true }, received);
    }

    [TestMethod]
    public async Task NonBufferableInboundMessagesDispatchWhileBuffering()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var receivedCount = 0;
        service.RegisterMessageHandler<PeerInputMessage>(_ => receivedCount++);

        service.SetBufferMessages(true);
        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new PeerInputMessage(),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, receivedCount);
    }

    [TestMethod]
    public async Task BufferedInboundMessagesStillWaitForExactHandlerRegistration()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var receivedCount = 0;

        service.SetBufferMessages(true);
        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        service.SetBufferMessages(false);
        service.Update();

        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(_ => receivedCount++);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, receivedCount);
    }

    [TestMethod]
    public async Task EarlyJoinResponseAndCharacterStateFlushAfterHandlersRegister()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        var receivedTypes = new List<string>();

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new ClientLobbyJoinResponseMessage { playersInLobby = [], modifiers = [] },
            sequence: 1));
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: "client-1",
            new LobbyPlayerSetReadyMessage(),
            sequence: 2));
        await Task.Delay(50);
        service.Update();

        Assert.AreEqual(0, receivedTypes.Count);

        service.RegisterMessageHandler<ClientLobbyJoinResponseMessage>(_ => receivedTypes.Add(nameof(ClientLobbyJoinResponseMessage)));
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(_ => receivedTypes.Add(nameof(LobbyPlayerSetReadyMessage)));
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(
            new[] { nameof(ClientLobbyJoinResponseMessage), nameof(LobbyPlayerSetReadyMessage) },
            receivedTypes);
    }

    [TestMethod]
    public async Task OutboundDuringRemoteStateDispatchIsAllowed()
    {
        var transport = new QueuedTransport();
        var logs = new List<string>();
        BrokerBackedNetService? service = null;
        service = new BrokerBackedNetService("local-test", "client-1", 1, transport, logs.Add);
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>(_ => service.SendMessage(new LobbyPlayerSetReadyMessage()));

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(EnvelopeForMessage(
            "client-0",
            targetClientId: null,
            new LobbyPlayerSetReadyMessage(),
            sequence: 1));
        await Task.Delay(50);
        service.Update();
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        var sent = transport.Sent.Single();
        Assert.AreEqual(typeof(LobbyPlayerSetReadyMessage).AssemblyQualifiedName, sent.MessageType);
        Assert.IsNull(sent.TargetClientId);
        Assert.IsFalse(logs.Any(log => log.Contains("applying remote state", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReceiveLoopLogsHandlerExceptionsAndContinues()
    {
        var transport = new QueuedTransport();
        var logs = new List<string>();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport, logs.Add);
        var receivedSource = new TaskCompletionSource<FakeLobbyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RegisterMessageHandler<FakeLobbyMessage>(_ => throw new InvalidOperationException("handler boom"));
        service.RegisterMessageHandler<FakeLobbyMessage>(message => receivedSource.SetResult(message));

        var loop = service.RunReceiveLoopAsync(CancellationToken.None);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("first"),
            sequence: 1));
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            new FakeLobbyMessage("second"),
            sequence: 2));

        await Task.Delay(50);
        service.Update();
        var received = await receivedSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await transport.CompleteAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual("first", received.Kind);
        Assert.IsTrue(logs.Any(log => log.Contains("handler boom", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReceiveLoopStopsWhenCanceled()
    {
        var transport = new QueuedTransport();
        var service = new BrokerBackedNetService("local-test", "client-1", 1, transport);
        using var cancellation = new CancellationTokenSource();

        var loop = service.RunReceiveLoopAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsFalse(service.IsConnected);
    }

    [TestMethod]
    public async Task ClientUntargetedSendRemainsUntargetedForBrokerHostRouting()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-1",
            clientIndex: 1,
            role: Runtime.BrokerClientRole.Client,
            transport: transport);

        await service.SendMessageAsync(new FakeLobbyMessage("host-only"), targetPlayerId: null, CancellationToken.None);

        var envelope = transport.Sent.Single();
        Assert.IsNull(envelope.TargetClientId);
    }

    [TestMethod]
    public async Task HostBroadcastSendRemainsBroadcast()
    {
        var transport = new CapturingTransport();
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            role: Runtime.BrokerClientRole.Host,
            transport: transport);

        await service.SendMessageAsync(new FakeLobbyMessage("broadcast"), targetPlayerId: null, CancellationToken.None);

        var envelope = transport.Sent.Single();
        Assert.IsNull(envelope.TargetClientId);
    }

    [TestMethod]
    public async Task HostDispatchRebroadcastsClientBroadcastToReadyPeers()
    {
        var transport = new CapturingTransport(
        [
            new BrokerClientRegistrationInfo("client-2", Runtime.BrokerClientRole.Client, 2),
            new BrokerClientRegistrationInfo("client-3", Runtime.BrokerClientRole.Client, 3)
        ]);
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            role: Runtime.BrokerClientRole.Host,
            transport: transport);
        LobbyPlayerSetReadyMessage? received = null;
        ulong? senderId = null;
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>((message, sender) =>
        {
            received = message;
            senderId = sender;
        });
        service.SetPeerReadyForBroadcasting(BrokerPlayerId.ForClientIndex(2));
        var envelope = EnvelopeForMessage(
            "client-1",
            targetClientId: null,
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 42);

        await service.DispatchEnvelopeAsync(envelope, CancellationToken.None);

        Assert.IsNotNull(received);
        Assert.IsTrue(received.Value.ready);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(1), senderId);
        var forwarded = transport.Sent.Single();
        Assert.AreEqual(envelope.SessionId, forwarded.SessionId);
        Assert.AreEqual("client-1", forwarded.SourceClientId);
        Assert.AreEqual("client-2", forwarded.TargetClientId);
        Assert.AreEqual(envelope.MessageType, forwarded.MessageType);
        CollectionAssert.AreEqual(envelope.Payload, forwarded.Payload);
        Assert.AreEqual(42, forwarded.Sequence);
    }

    [TestMethod]
    public async Task HostDispatchDoesNotRebroadcastNonBroadcastNetMessages()
    {
        var transport = new CapturingTransport(
        [
            new BrokerClientRegistrationInfo("client-2", Runtime.BrokerClientRole.Client, 2)
        ]);
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            role: Runtime.BrokerClientRole.Host,
            transport: transport);
        service.RegisterMessageHandler<ClientLobbyJoinResponseMessage>(_ => { });
        service.SetPeerReadyForBroadcasting(BrokerPlayerId.ForClientIndex(2));

        await service.DispatchEnvelopeAsync(EnvelopeForMessage(
            "client-1",
            targetClientId: null,
            new ClientLobbyJoinResponseMessage { playersInLobby = [], modifiers = [] },
            sequence: 43), CancellationToken.None);

        Assert.AreEqual(0, transport.Sent.Count);
    }

    [TestMethod]
    public async Task ClientDispatchDoesNotRebroadcastClientBroadcastMessages()
    {
        var transport = new CapturingTransport(
        [
            new BrokerClientRegistrationInfo("client-2", Runtime.BrokerClientRole.Client, 2)
        ]);
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-0",
            clientIndex: 0,
            role: Runtime.BrokerClientRole.Client,
            transport: transport);
        service.RegisterMessageHandler<LobbyPlayerSetReadyMessage>((_, _) => { });
        service.SetPeerReadyForBroadcasting(BrokerPlayerId.ForClientIndex(2));

        await service.DispatchEnvelopeAsync(EnvelopeForMessage(
            "client-1",
            targetClientId: null,
            new LobbyPlayerSetReadyMessage { ready = true },
            sequence: 44), CancellationToken.None);

        Assert.AreEqual(0, transport.Sent.Count);
    }

    [TestMethod]
    public void ClientServiceDoesNotAssumeClientZeroIsConnectedHost()
    {
        var service = new BrokerBackedNetService(
            sessionId: "local-test",
            clientId: "client-1",
            clientIndex: 1,
            role: Runtime.BrokerClientRole.Client,
            transport: new CapturingTransport());

        Assert.AreEqual(0, service.ConnectedPeerIds.Count);
    }

    [TestMethod]
    public async Task BrokerClientTransportSendsThroughConnection()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        await using var host = await Runtime.BrokerClientConnection.ConnectAsync(
            new Runtime.BrokerClientConfig(Runtime.BrokerClientRole.Host, 0, "127.0.0.1", server.Port, "local-test"),
            "client-0",
            CancellationToken.None);
        await using var client = await LocalCoop.Protocol.BrokerClientConnection.ConnectAsync(
            new LocalCoop.Protocol.BrokerClientConfig(LocalCoop.Protocol.BrokerClientRole.Client, 1, "127.0.0.1", server.Port, "local-test"),
            "client-1",
            CancellationToken.None);
        var service = new BrokerBackedNetService(
            "local-test",
            "client-0",
            0,
            new BrokerClientEnvelopeTransport(host));

        await service.SendMessageAsync(new FakeLobbyMessage("ready"), BrokerPlayerId.ForClientIndex(1), CancellationToken.None);

        var received = await client.ReadEnvelopeAsync(CancellationToken.None);
        Assert.IsNotNull(received);
        Assert.AreEqual(typeof(FakeLobbyMessage).AssemblyQualifiedName, received.MessageType);
    }

    private readonly record struct FakeLobbyMessage(string Kind);

    private struct FieldLobbyMessage
    {
        public string? kind;
    }

    private struct PacketSerializableMessage : IPacketSerializable
    {
        public string? value;
        public IntPtr unsupported;

        public void Serialize(PacketWriter writer)
        {
            writer.WriteString(value ?? string.Empty);
        }

        public void Deserialize(PacketReader reader)
        {
            value = reader.ReadString();
        }
    }

    private static Runtime.BrokerEnvelope EnvelopeFor<T>(string sourceClientId, string? targetClientId, long sequence)
    {
        return new Runtime.BrokerEnvelope(
            "local-test",
            sourceClientId,
            targetClientId,
            typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            [],
            sequence);
    }

    private static Runtime.BrokerEnvelope EnvelopeForMessage<T>(string sourceClientId, string? targetClientId, T message, long sequence)
    {
        return BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            sourceClientId,
            targetClientId,
            message,
            sequence);
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public CapturingTransport(IReadOnlyList<BrokerClientRegistrationInfo>? connectedPeers = null)
        {
            ConnectedPeers = connectedPeers ?? [];
        }

        public List<Runtime.BrokerEnvelope> Sent { get; } = [];

        public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers { get; }

        public Task SendEnvelopeAsync(Runtime.BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            Sent.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<Runtime.BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Runtime.BrokerEnvelope?>(null);
        }
    }

    private sealed class QueuedTransport : IBrokerEnvelopeTransport
    {
        private readonly Channel<Runtime.BrokerEnvelope?> _incoming = Channel.CreateUnbounded<Runtime.BrokerEnvelope?>();

        public List<Runtime.BrokerEnvelope> Sent { get; } = [];

        public Task SendEnvelopeAsync(Runtime.BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            Sent.Add(envelope);
            return Task.CompletedTask;
        }

        public async Task<Runtime.BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }

        public async Task QueueEnvelopeAsync(Runtime.BrokerEnvelope envelope)
        {
            await _incoming.Writer.WriteAsync(envelope);
        }

        public async Task CompleteAsync()
        {
            await _incoming.Writer.WriteAsync(null);
        }
    }
}

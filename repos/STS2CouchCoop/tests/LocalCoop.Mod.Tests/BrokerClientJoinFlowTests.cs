using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Channels;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerClientJoinFlowTests
{
    [TestMethod]
    public void ShouldUseBrokerJoinForAnyEnabledBrokerMode()
    {
        var clientSettings = Settings(BrokerClientRole.Client);
        var hostSettings = Settings(BrokerClientRole.Host);

        Assert.IsTrue(BrokerClientJoinFlow.ShouldUseBrokerJoin(clientSettings));
        Assert.IsTrue(BrokerClientJoinFlow.ShouldUseBrokerJoin(hostSettings));
        Assert.IsFalse(BrokerClientJoinFlow.ShouldUseBrokerJoin(new BrokerModeSettings(false, null, "client-0", "events.txt", null)));
    }

    [TestMethod]
    public void PlaceholderInitializerCompletesWithoutSteamConnection()
    {
        var initializer = new BrokerClientJoinFlow.PlaceholderClientConnectionInitializer();

        var result = initializer.Connect(null!, CancellationToken.None).Result;

        Assert.IsFalse(result.HasValue);
    }

    [TestMethod]
    public async Task BeginStandardBrokerJoinSendsJoinRequestAndReturnsRealHostResponse()
    {
        var settings = Settings(BrokerClientRole.Client);
        var transport = new QueuedTransport();
        var response = new ClientLobbyJoinResponseMessage
        {
            playersInLobby = [],
            modifiers = [],
            seed = "HOSTSEED"
        };

        var joinTask = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => transport,
            _ => { },
            CancellationToken.None,
            () => BrokerClientJoinFlow.CreateJoinRequest(0, new SerializableUnlockState()));

        Assert.AreEqual(0, transport.Sent.Count);
        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            ValidInitialGameInfo(),
            sequence: 6));

        await WaitForAsync(() => Task.FromResult(transport.Sent.Count == 1));
        Assert.AreEqual(typeof(ClientLobbyJoinRequestMessage).AssemblyQualifiedName, transport.Sent.Single().MessageType);
        Assert.AreEqual("client-1", transport.Sent.Single().SourceClientId);
        Assert.IsNull(transport.Sent.Single().TargetClientId);

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            response,
            sequence: 7));

        var result = await joinTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(GameMode.Standard, result.gameMode);
        Assert.AreEqual(RunSessionState.InLobby, result.sessionState);
        Assert.IsTrue(result.joinResponse.HasValue);
        Assert.AreEqual("HOSTSEED", result.joinResponse.Value.seed);
        Assert.AreEqual(0, result.joinResponse.Value.playersInLobby?.Count);
        Assert.IsTrue(BrokerPendingNetGameServiceRegistry.TryTake(settings.ClientId, out var pending));
        Assert.AreEqual(NetGameType.Client, pending!.Type);
        pending.Dispose();
    }

    [TestMethod]
    public async Task BeginStandardBrokerJoinSendsLoadJoinRequestAndReturnsLoadedRunResponse()
    {
        var settings = Settings(BrokerClientRole.Client);
        var transport = new QueuedTransport();
        var response = new ClientLoadJoinResponseMessage
        {
            serializableRun = MinimalSerializableRun(),
            playersAlreadyConnected = [BrokerPlayerId.ForClientIndex(0)]
        };
        var initialInfo = ValidInitialGameInfo();
        initialInfo.sessionState = RunSessionState.InLoadedLobby;

        var joinTask = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => transport,
            _ => { },
            CancellationToken.None,
            () => BrokerClientJoinFlow.CreateJoinRequest(0, new SerializableUnlockState()));

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            initialInfo,
            sequence: 6));

        await WaitForAsync(() => Task.FromResult(transport.Sent.Count == 1));
        Assert.AreEqual(typeof(ClientLoadJoinRequestMessage).AssemblyQualifiedName, transport.Sent.Single().MessageType);

        await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
            "local-test",
            "client-0",
            targetClientId: "client-1",
            response,
            sequence: 7));

        var result = await joinTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(GameMode.Standard, result.gameMode);
        Assert.AreEqual(RunSessionState.InLoadedLobby, result.sessionState);
        Assert.IsFalse(result.joinResponse.HasValue);
        Assert.IsTrue(result.loadJoinResponse.HasValue);
        Assert.AreEqual(1, result.loadJoinResponse.Value.playersAlreadyConnected.Count);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), result.loadJoinResponse.Value.playersAlreadyConnected.Single());
        Assert.IsTrue(BrokerPendingNetGameServiceRegistry.TryTake(settings.ClientId, out var pending));
        Assert.AreEqual(NetGameType.Client, pending!.Type);
        pending.Dispose();
    }

    [TestMethod]
    public async Task BeginStandardBrokerJoinReceivesJoinResponseBehindInitialGameInfo()
    {
        var settings = Settings(BrokerClientRole.Client);
        var transport = new QueuedTransport();
        using var joinCancellation = new CancellationTokenSource();
        var response = new ClientLobbyJoinResponseMessage
        {
            playersInLobby = [],
            modifiers = [],
            seed = "HOSTSEED"
        };

        var joinTask = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => transport,
            _ => { },
            joinCancellation.Token,
            () => BrokerClientJoinFlow.CreateJoinRequest(0, new SerializableUnlockState()));

        try
        {
            Assert.AreEqual(0, transport.Sent.Count);
            await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
                "local-test",
                "client-0",
                targetClientId: "client-1",
                ValidInitialGameInfo(),
                sequence: 6));
            await WaitForAsync(() => Task.FromResult(transport.Sent.Count == 1));
            await transport.QueueEnvelopeAsync(BrokerEnvelopeMessageSerializer.ToEnvelope(
                "local-test",
                "client-0",
                targetClientId: "client-1",
                response,
                sequence: 7));

            var result = await joinTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsTrue(result.joinResponse.HasValue);
            Assert.AreEqual("HOSTSEED", result.joinResponse.Value.seed);
            Assert.IsTrue(BrokerPendingNetGameServiceRegistry.TryTake(settings.ClientId, out var pending));
            var initialInfoCount = 0;
            pending!.RegisterMessageHandler<InitialGameInfoMessage>((_, _) => initialInfoCount++);
            pending.Update();
            Assert.AreEqual(0, initialInfoCount);
            pending.Dispose();
        }
        finally
        {
            if (!joinTask.IsCompleted)
            {
                await joinCancellation.CancelAsync();
                try
                {
                    await joinTask;
                }
                catch
                {
                }
            }

            BrokerPendingNetGameServiceRegistry.ClearForTesting();
        }
    }

    [TestMethod]
    public void CreateJoinRequestUsesProgressAndUnlockState()
    {
        var unlockState = new SerializableUnlockState();

        var request = BrokerClientJoinFlow.CreateJoinRequest(7, unlockState);

        Assert.AreEqual(7, request.maxAscensionUnlocked);
        Assert.AreEqual(unlockState, request.unlockState);
    }

    [TestMethod]
    public void TryHideJoinScreenInvokesGodotVisibleSetter()
    {
        var screen = new FakeJoinScreen();
        var messages = new List<string>();

        BrokerClientJoinFlow.TryHideJoinScreen(screen, messages.Add);

        Assert.IsFalse(screen.VisibleState);
        StringAssert.Contains(messages.Single(), "hiding join screen");
    }

    [TestMethod]
    public void TryHideJoinScreenPopsScreenFromSubmenuStackWhenItIsOnTop()
    {
        var screen = new FakeJoinScreenWithStack();
        var messages = new List<string>();

        BrokerClientJoinFlow.TryHideJoinScreen(screen, messages.Add);

        Assert.AreEqual(1, screen.Stack.PopCount);
        Assert.IsFalse(screen.VisibleState);
        Assert.IsTrue(messages.Any(message => message.Contains("popped join screen", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TryTriggerBrokerJoinFromOpenedJoinScreenInvokesJoinAndHidesScreenWhenBrokerEnabled()
    {
        var screen = new FakeOpenedJoinScreen();
        var messages = new List<string>();

        var triggered = BrokerClientJoinFlow.TryTriggerBrokerJoinFromOpenedJoinScreen(screen, Settings(BrokerClientRole.Client), messages.Add);

        Assert.IsTrue(triggered);
        Assert.AreEqual(1, screen.JoinGameCount);
        Assert.IsInstanceOfType<BrokerClientJoinFlow.PlaceholderClientConnectionInitializer>(screen.LastInitializer);
        Assert.IsFalse(screen.VisibleState);
        Assert.IsTrue(messages.Any(message => message.Contains("triggering broker client join", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(message => message.Contains("hiding join screen", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TryTriggerBrokerJoinFromOpenedJoinScreenDoesNothingWhenBrokerDisabled()
    {
        var screen = new FakeOpenedJoinScreen();
        var settings = new BrokerModeSettings(false, null, "client-1", "events.txt", null);

        var triggered = BrokerClientJoinFlow.TryTriggerBrokerJoinFromOpenedJoinScreen(screen, settings, _ => { });

        Assert.IsFalse(triggered);
        Assert.AreEqual(0, screen.JoinGameCount);
        Assert.IsTrue(screen.VisibleState);
    }

    private static BrokerModeSettings Settings(BrokerClientRole role)
    {
        return new BrokerModeSettings(
            true,
            new BrokerClientConfig(role, role == BrokerClientRole.Host ? 0 : 1, "127.0.0.1", 38993, "local-test"),
            role == BrokerClientRole.Host ? "client-0" : "client-1",
            "events.txt",
            null);
    }

    private static InitialGameInfoMessage ValidInitialGameInfo()
    {
        return new InitialGameInfoMessage
        {
            version = "test",
            idDatabaseHash = 0,
            gameplayAffectingMods = [],
            otherMods = [],
            gameMode = GameMode.Standard,
            sessionState = RunSessionState.InLobby,
            connectionFailureReason = null
        };
    }

    private static SerializableRun MinimalSerializableRun()
    {
        return new SerializableRun
        {
            SchemaVersion = 1,
            Acts = [],
            Modifiers = [],
            EventsSeen = [],
            GameMode = GameMode.Standard,
            CurrentActIndex = 0,
            SerializableOdds = new SerializableRunOddsSet(),
            Players = [],
            SerializableRng = new SerializableRunRngSet
            {
                Seed = "TESTSEED",
                Counters = []
            },
            SerializableSharedRelicGrabBag = new SerializableRelicGrabBag
            {
                RelicIdLists = []
            },
            VisitedMapCoords = [],
            MapPointHistory = [],
            ExtraFields = new SerializableExtraRunFields()
        };
    }

    private sealed class FakeJoinScreen
    {
        public bool VisibleState { get; private set; } = true;

        public void set_Visible(bool visible)
        {
            VisibleState = visible;
        }
    }

    private sealed class FakeJoinScreenWithStack
    {
        private readonly FakeSubmenuStack _stack;

        public FakeJoinScreenWithStack()
        {
            _stack = new FakeSubmenuStack(this);
        }

        public FakeSubmenuStack Stack => _stack;

        public bool VisibleState { get; private set; } = true;

        public void set_Visible(bool visible)
        {
            VisibleState = visible;
        }
    }

    private sealed class FakeSubmenuStack(object top)
    {
        public int PopCount { get; private set; }

        public object Peek()
        {
            return top;
        }

        public void Pop()
        {
            PopCount++;
        }
    }

    private sealed class FakeOpenedJoinScreen
    {
        public int JoinGameCount { get; private set; }

        public object? LastInitializer { get; private set; }

        public bool VisibleState { get; private set; } = true;

        public void JoinGame(object initializer)
        {
            JoinGameCount++;
            LastInitializer = initializer;
        }

        public void set_Visible(bool visible)
        {
            VisibleState = visible;
        }
    }

    private sealed class QueuedTransport : IBrokerEnvelopeTransport
    {
        private readonly Channel<BrokerEnvelope?> _incoming = Channel.CreateUnbounded<BrokerEnvelope?>();

        public List<BrokerEnvelope> Sent { get; } = [];

        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            Sent.Add(envelope);
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

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Condition was not satisfied before timeout.");
    }
}

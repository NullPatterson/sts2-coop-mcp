using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class TransportSeamProbeTests
{
    [TestMethod]
    public void ProbeFindsNetworkTypesAndInterestingMethods()
    {
        var result = TransportSeamProbe.Run([typeof(FakeNetGameService).Assembly]);

        var labels = result.Entries.Select(entry => entry.Label).ToArray();

        CollectionAssert.Contains(labels, "net game service");
        CollectionAssert.Contains(labels, "steam host transport");
        CollectionAssert.Contains(labels, "character select screen");
        Assert.IsTrue(result.Entries.Any(entry => entry.Members.Any(member => member.Contains("SendMessage"))));
        Assert.IsTrue(result.Entries.Any(entry => entry.Members.Any(member => member.Contains("InitializeMultiplayerAsHost"))));
        Assert.IsTrue(result.Entries.Any(entry => entry.Members.Any(member => member.Contains("ctor") && member.Contains("FakeNetGameService"))));
        Assert.IsTrue(result.Entries.Any(entry => entry.Members.Any(member => member.Contains("CreateNetGameService"))));
    }

    [TestMethod]
    public void ProbePrefersConcreteLobbyAndCharacterSelectClassesOverInterfaces()
    {
        var result = TransportSeamProbe.Run([typeof(FakeNetGameService).Assembly]);

        Assert.AreEqual(
            typeof(FakeStartRunLobby).FullName,
            result.Entries.Single(entry => entry.Label == "start run lobby").TypeName);
        Assert.AreEqual(
            typeof(FakeCharacterSelectScreen).FullName,
            result.Entries.Single(entry => entry.Label == "character select screen").TypeName);
    }

    [TestMethod]
    public void PassiveFormatterSummarizesLobbyLifecycle()
    {
        var line = PassiveTransportDiagnostics.FormatLobbyLifecycle(
            typeName: "Fake.CharacterSelect",
            methodName: "InitializeMultiplayerAsHost",
            args: [new FakeNetGameService(), 4]);

        StringAssert.Contains(line, "Fake.CharacterSelect.InitializeMultiplayerAsHost");
        StringAssert.Contains(line, "FakeNetGameService");
        StringAssert.Contains(line, "4");
    }

    private sealed class FakeNetGameService
    {
        public ulong NetId => 42;
        public void RegisterMessageHandler<T>(Action<T> handler) { }
        public void SendMessage<T>(T message, ulong playerId) { }
    }

    private sealed class FakeNetHostGameService
    {
        public IReadOnlyList<ulong> ConnectedPeers => [];
        public void SetPeerReadyForBroadcasting(ulong peerId) { }
    }

    private sealed class FakeStartRunLobby
    {
        public FakeStartRunLobby(FakeNetGameService service)
        {
        }

        public static FakeNetGameService CreateNetGameService()
        {
            return new FakeNetGameService();
        }
    }

    private interface IFakeStartRunLobbyListener
    {
    }

    private sealed class FakeSteamHost
    {
        public void SendMessageToClient(ulong playerId, object message) { }
    }

    private sealed class FakeSteamClient
    {
        public void ReceiveMessage(object message) { }
    }

    private sealed class FakeCharacterSelectScreen
    {
        public void InitializeMultiplayerAsHost(FakeNetGameService service, int maxPlayers) { }
        public void InitializeMultiplayerAsClient(FakeNetGameService service) { }
    }

    private interface IFakeCharacterSelectButtonDelegate
    {
    }
}

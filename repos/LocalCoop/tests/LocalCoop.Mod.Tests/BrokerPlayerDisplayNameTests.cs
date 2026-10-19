using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerPlayerDisplayNameTests
{
    [TestCleanup]
    public void Cleanup()
    {
        BrokerPlayerDisplayNames.ClearForTesting();
    }

    [TestMethod]
    public void TryGetNameReturnsFalseForNativePlayerIds()
    {
        Assert.IsFalse(BrokerPlayerDisplayNames.TryGetName(1234, out _));
    }

    [TestMethod]
    public void RegisterCharacterGivesBrokerPlayersCharacterSkewedNames()
    {
        var playerId = BrokerPlayerId.ForClientIndex(1);

        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Silent());

        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var name));
        StringAssert.Matches(name, BrokerPlayerDisplayNames.CharacterNamePatternForTesting("Silent"));
    }

    [TestMethod]
    public void SamePlayerKeepsStableNameForSameCharacter()
    {
        var playerId = BrokerPlayerId.ForClientIndex(2);

        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Defect());
        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var first));
        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Defect());
        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var second));

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void ChangingCharacterChangesGeneratedNameProfile()
    {
        var playerId = BrokerPlayerId.ForClientIndex(3);

        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Ironclad());
        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var ironcladName));
        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Regent());
        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var regentName));

        Assert.AreNotEqual(ironcladName, regentName);
        StringAssert.Matches(regentName, BrokerPlayerDisplayNames.CharacterNamePatternForTesting("Regent"));
    }

    [TestMethod]
    public void UnknownCharacterUsesGenericFantasyNames()
    {
        var playerId = BrokerPlayerId.ForClientIndex(0);

        BrokerPlayerDisplayNames.RegisterCharacter(playerId, character: null);

        Assert.IsTrue(BrokerPlayerDisplayNames.TryGetName(playerId, out var name));
        StringAssert.Matches(name, BrokerPlayerDisplayNames.CharacterNamePatternForTesting("Unknown"));
    }

    [TestMethod]
    public void PlatformNamePatchOnlyOverridesBrokerIds()
    {
        var playerId = BrokerPlayerId.ForClientIndex(1);
        BrokerPlayerDisplayNames.RegisterCharacter(playerId, new Necrobinder());

        Assert.IsTrue(BrokerPlayerDisplayNamePlatformPatch.TryOverrideNameForTesting(playerId, out var brokerName));
        Assert.IsFalse(BrokerPlayerDisplayNamePlatformPatch.TryOverrideNameForTesting(42, out _));
        StringAssert.Matches(brokerName, BrokerPlayerDisplayNames.CharacterNamePatternForTesting("Necrobinder"));
    }

    [TestMethod]
    public void NameplatePatchRefreshesNameFromChangedLobbyPlayer()
    {
        var playerId = BrokerPlayerId.ForClientIndex(2);
        var nameplate = new FakeNameplateLabel();
        var node = new FakeRemoteLobbyPlayer(playerId, new Ironclad(), nameplate);
        var lobbyPlayer = new LobbyPlayer
        {
            id = playerId,
            character = new Silent()
        };

        var refreshed = BrokerPlayerDisplayNameNameplatePatch.RefreshNameplateForTesting(node, lobbyPlayer);

        Assert.IsTrue(refreshed);
        Assert.IsNotNull(nameplate.Text);
        StringAssert.Matches(nameplate.Text, BrokerPlayerDisplayNames.CharacterNamePatternForTesting("Silent"));
        Assert.AreEqual(playerId, node.PlayerId);
    }

    [TestMethod]
    public void NameplatePatchLeavesNativePlayerIdsUntouched()
    {
        var nameplate = new FakeNameplateLabel();
        var node = new FakeRemoteLobbyPlayer(42, new Silent(), nameplate);

        var refreshed = BrokerPlayerDisplayNameNameplatePatch.RefreshNameplateForTesting(node, null);

        Assert.IsFalse(refreshed);
        Assert.IsNull(nameplate.Text);
    }

    private sealed class FakeRemoteLobbyPlayer
    {
        private readonly ulong _playerId;
        private readonly CharacterModel _character;
        private readonly FakeNameplateLabel _nameplateLabel;

        public FakeRemoteLobbyPlayer(ulong playerId, CharacterModel character, FakeNameplateLabel nameplateLabel)
        {
            _playerId = playerId;
            _character = character;
            _nameplateLabel = nameplateLabel;
        }

        public ulong PlayerId => _playerId;
    }

    private sealed class FakeNameplateLabel
    {
        public string? Text { get; private set; }

        public void SetTextAutoSize(string text)
        {
            Text = text;
        }
    }
}

using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Channels;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerLobbyServiceSubstitutionTests
{
    [TestMethod]
    public void ResolveRoleForLifecycleUsesSts2LifecycleInsteadOfConfigRole()
    {
        Assert.AreEqual(
            BrokerClientRole.Host,
            BrokerLobbyServiceSubstitution.ResolveRoleForLifecycle("InitializeMultiplayerAsHost"));
        Assert.AreEqual(
            BrokerClientRole.Client,
            BrokerLobbyServiceSubstitution.ResolveRoleForLifecycle("InitializeMultiplayerAsClient"));
        Assert.AreEqual(
            BrokerClientRole.Host,
            BrokerLobbyServiceSubstitution.ResolveRoleForLifecycle("InitializeAsHost"));
        Assert.AreEqual(
            BrokerClientRole.Client,
            BrokerLobbyServiceSubstitution.ResolveRoleForLifecycle("InitializeAsClient"));
        Assert.IsNull(BrokerLobbyServiceSubstitution.ResolveRoleForLifecycle("InitializeSinglePlayer"));
    }

    [TestMethod]
    public void PatchTargetsCharacterSelectAndLoadedRunLifecycleScreens()
    {
        var targets = LocalCoop.Mod.Patches.BrokerLobbyServiceSubstitutionPatch.TargetMethods()
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .OrderBy(name => name)
            .ToArray();

        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen.InitializeMultiplayerAsHost");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen.InitializeMultiplayerAsClient");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen.InitializeAsHost");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen.InitializeAsClient");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunLoadScreen.InitializeAsHost");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunLoadScreen.InitializeAsClient");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunLoadScreen.InitializeAsHost");
        CollectionAssert.Contains(targets, "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunLoadScreen.InitializeAsClient");
    }

    [TestMethod]
    public void TrySubstituteFirstArgumentReplacesServiceWhenBrokerModeIsEnabled()
    {
        var originalService = new object();
        object?[] args = [originalService, 4];
        var settings = EnabledSettings(BrokerClientRole.Client);

        var substituted = BrokerLobbyServiceSubstitution.TrySubstituteFirstArgument(
            settings,
            args,
            BrokerClientRole.Host,
            () => new CapturingTransport(),
            _ => { });

        Assert.IsTrue(substituted);
        Assert.AreNotSame(originalService, args[0]);
        Assert.AreEqual(BrokerPlayerId.ForClientIndex(0), args[0]!.GetType().GetProperty("NetId")!.GetValue(args[0]));
        Assert.AreEqual(NetGameType.Host, args[0]!.GetType().GetProperty("Type")!.GetValue(args[0]));
    }

    [TestMethod]
    public void TrySubstituteFirstArgumentUsesClientLifecycleRoleWhenConfigSaysHost()
    {
        object?[] args = [new object()];
        var settings = EnabledSettings(BrokerClientRole.Host);

        var substituted = BrokerLobbyServiceSubstitution.TrySubstituteFirstArgument(
            settings,
            args,
            BrokerClientRole.Client,
            () => new CapturingTransport(),
            _ => { });

        Assert.IsTrue(substituted);
        Assert.AreEqual(NetGameType.Client, args[0]!.GetType().GetProperty("Type")!.GetValue(args[0]));
    }

    [TestMethod]
    public void CreateRegistrationConfigUsesEffectiveLifecycleRole()
    {
        var settings = EnabledSettings(BrokerClientRole.Client);

        var config = BrokerLobbyServiceSubstitution.CreateRegistrationConfig(settings, BrokerClientRole.Host);

        Assert.AreEqual(BrokerClientRole.Host, config.Role);
        Assert.AreEqual(0, config.ClientIndex);
        Assert.AreEqual("local-test", config.SessionId);
    }

    [TestMethod]
    public void TrySubstituteFirstArgumentLeavesArgumentsWhenBrokerModeIsDisabled()
    {
        var originalService = new object();
        object?[] args = [originalService, 4];
        var settings = new BrokerModeSettings(false, null, "client-0", "events.txt", null);

        var substituted = BrokerLobbyServiceSubstitution.TrySubstituteFirstArgument(
            settings,
            args,
            BrokerClientRole.Host,
            () => throw new InvalidOperationException("transport should not be created"),
            _ => { });

        Assert.IsFalse(substituted);
        Assert.AreSame(originalService, args[0]);
    }

    [TestMethod]
    public void TrySubstituteFirstArgumentReusesPendingClientJoinService()
    {
        var settings = EnabledSettings(BrokerClientRole.Client);
        var inner = new BrokerBackedNetService("local-test", settings.ClientId, settings.Config!.ClientIndex, new CapturingTransport());
        var service = new BrokerNetGameService(inner, NetGameType.Client);
        object?[] args = [service];
        BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);

        var substituted = BrokerLobbyServiceSubstitution.TrySubstituteFirstArgument(
            settings,
            args,
            BrokerClientRole.Client,
            () => throw new InvalidOperationException("pending client service should be reused"),
            _ => { });

        Assert.IsTrue(substituted);
        Assert.AreSame(service, args[0]);
        service.Dispose();
    }

    private static BrokerModeSettings EnabledSettings(BrokerClientRole role)
    {
        return new BrokerModeSettings(
            true,
            new BrokerClientConfig(role, 0, "127.0.0.1", 38989, "local-test"),
            "client-0",
            "events.txt",
            null);
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<BrokerEnvelope?>(null);
        }
    }

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

        public async Task CompleteAsync()
        {
            await _incoming.Writer.WriteAsync(null);
        }
    }
}

using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerClientJoinFlowPatch
{
    private static readonly TimeSpan BrokerConnectTimeout = TimeSpan.FromSeconds(3);

    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
        return type is null
            ? null
            : AccessTools.Method(type, "Begin", [typeof(IClientConnectionInitializer), AccessTools.TypeByName("Godot.SceneTree")]);
    }

    public static bool Prefix(ref Task<JoinResult> __result)
    {
        var settings = LoadSettings();
        if (!BrokerClientJoinFlow.ShouldUseBrokerJoin(settings))
        {
            return true;
        }

        var log = new BrokerEventLog(settings.EventLogPath);
        __result = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => CreateTransport(settings),
            log.Write,
            CancellationToken.None);
        return false;
    }

    private static IBrokerEnvelopeTransport CreateTransport(BrokerModeSettings settings)
    {
        var config = BrokerLobbyServiceSubstitution.CreateRegistrationConfig(settings, BrokerClientRole.Client);
        return BrokerEnvelopeTransportConnector.ConnectBlocking(
            config,
            settings.ClientId,
            BrokerConnectTimeout,
            CancellationToken.None);
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

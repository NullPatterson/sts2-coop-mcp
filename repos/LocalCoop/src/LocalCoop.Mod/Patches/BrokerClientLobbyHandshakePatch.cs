using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerClientLobbyHandshakePatch
{
    private static readonly ConditionalWeakTable<object, TriggeredMarker> TriggeredScreens = new();

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen");
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name == "InitializeMultiplayerAsClient"))
        {
            yield return method;
        }
    }

    public static void Postfix(object __instance, object[] __args)
    {
        var settings = LoadSettings();
        if (!BrokerClientJoinFlow.ShouldUseBrokerJoin(settings))
        {
            return;
        }

        var log = new BrokerEventLog(settings.EventLogPath);
        if (TriggeredScreens.TryGetValue(__instance, out _))
        {
            log.Write("Broker client lobby handshake: join request already sent for this screen.");
            return;
        }

        try
        {
            var gameService = __args.OfType<INetGameService>().FirstOrDefault();
            if (gameService is null)
            {
                log.Write("Broker client lobby handshake: INetGameService argument unavailable.");
                return;
            }

            TriggeredScreens.Add(__instance, new TriggeredMarker());
            gameService.SendMessage(BrokerClientJoinFlow.CreateJoinRequestFromCurrentSave(log.Write));
            log.Write("Broker client lobby handshake: sent ClientLobbyJoinRequestMessage.");
        }
        catch (Exception exception)
        {
            log.Write($"Broker client lobby handshake failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }

    private sealed class TriggeredMarker;
}

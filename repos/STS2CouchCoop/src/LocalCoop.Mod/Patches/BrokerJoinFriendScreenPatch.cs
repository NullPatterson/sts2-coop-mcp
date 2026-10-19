using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerJoinFriendScreenPatch
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
        return type is null ? null : AccessTools.Method(type, "OnJoinFriendsPressed");
    }

    public static void Postfix(object? __result)
    {
        var settings = LoadSettings();
        if (!BrokerClientJoinFlow.ShouldUseBrokerJoin(settings))
        {
            return;
        }

        var log = new BrokerEventLog(settings.EventLogPath);
        BrokerClientJoinFlow.TryTriggerBrokerJoinFromOpenedJoinScreen(__result, settings, log.Write);
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

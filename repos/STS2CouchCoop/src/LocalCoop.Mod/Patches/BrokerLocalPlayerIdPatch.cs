using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Platform;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerLocalPlayerIdPatch
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Platform.PlatformUtil");
        return type is null
            ? null
            : AccessTools.Method(type, "GetLocalPlayerId", [typeof(PlatformType)]);
    }

    public static bool Prefix(ref ulong __result)
    {
        var settings = LoadSettings();
        if (!BrokerLocalPlayerIdentity.TryGetLocalPlayerId(settings, out var playerId))
        {
            return true;
        }

        __result = playerId;
        return false;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

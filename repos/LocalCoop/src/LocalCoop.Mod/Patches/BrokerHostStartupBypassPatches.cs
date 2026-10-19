using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerHostSteamStartupBypassPatch
{
    private const string NativeMethodName = "StartSteamHost";

    public static MethodBase? TargetMethod()
    {
        return ResolveNativeHostMethod(NativeMethodName);
    }

    public static bool Prefix(ref Task<NetErrorInfo?> __result)
    {
        var settings = LoadSettings();
        if (!BrokerHostStartupBypass.TrySkipNativeHostStartup(
                settings,
                NativeMethodName,
                new BrokerEventLog(settings.EventLogPath).Write))
        {
            return true;
        }

        __result = Task.FromResult<NetErrorInfo?>(null);
        return false;
    }

    internal static MethodBase? ResolveNativeHostMethod(string methodName)
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService");
        return type is null ? null : AccessTools.Method(type, methodName);
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

[HarmonyPatch]
public static class BrokerHostENetStartupBypassPatch
{
    private const string NativeMethodName = "StartENetHost";

    public static MethodBase? TargetMethod()
    {
        return BrokerHostSteamStartupBypassPatch.ResolveNativeHostMethod(NativeMethodName);
    }

    public static bool Prefix(ref NetErrorInfo? __result)
    {
        var settings = LoadSettings();
        if (!BrokerHostStartupBypass.TrySkipNativeHostStartup(
                settings,
                NativeMethodName,
                new BrokerEventLog(settings.EventLogPath).Write))
        {
            return true;
        }

        __result = null;
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

using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class LobbyLifecycleDiagnosticsPatches
{
    private static readonly string[] TypeNames =
    [
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen",
        "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen",
        "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen"
    ];

    private static readonly string[] MethodNames =
    [
        "InitializeMultiplayerAsHost",
        "InitializeMultiplayerAsClient"
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var typeName in TypeNames)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type is null)
            {
                continue;
            }

            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => MethodNames.Contains(method.Name)))
            {
                yield return method;
            }
        }
    }

    public static void Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        var settings = LoadSettings();
        if (!settings.Enabled)
        {
            return;
        }

        new BrokerEventLog(settings.EventLogPath).Write(PassiveTransportDiagnostics.FormatLobbyLifecycle(
            __instance.GetType().FullName ?? __instance.GetType().Name,
            __originalMethod.Name,
            __args));
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}


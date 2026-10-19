using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class CharacterSelectInputDiagnosticsPatches
{
    private static readonly (string TypeName, string[] MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen",
            ["_Input", "SelectCharacter", "OnEmbarkPressed", "OnUnreadyPressed"]
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton",
            ["OnPress", "OnFocus"]
        )
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (typeName, methodNames) in Targets)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type is null)
            {
                continue;
            }

            foreach (var methodName in methodNames)
            {
                var method = AccessTools.Method(type, methodName);
                if (method is not null)
                {
                    yield return method;
                }
            }
        }
    }

    public static void Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        var settings = LoadSettings();
        if (!settings.Enabled || !ShouldLog(__originalMethod, __args))
        {
            return;
        }

        var typeName = __instance.GetType().FullName ?? __instance.GetType().Name;
        new BrokerEventLog(settings.EventLogPath).Write(
            CharacterSelectInputDiagnostics.FormatEvent(typeName, __originalMethod.Name, __instance, __args));
    }

    private static bool ShouldLog(MethodBase method, object?[] args)
    {
        if (!string.Equals(method.Name, "_Input", StringComparison.Ordinal))
        {
            return true;
        }

        var inputEvent = args.FirstOrDefault();
        if (inputEvent is null)
        {
            return false;
        }

        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        if (typeName.Contains("InputEventAction", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("InputEventJoypad", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsPressed(inputEvent);
    }

    private static bool IsPressed(object inputEvent)
    {
        var property = inputEvent.GetType().GetProperty(
            "Pressed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(inputEvent) is true;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

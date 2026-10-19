using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class ControllerInputBackgroundPollingPatches
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager");
        return type is null ? null : AccessTools.Method(type, "_Process");
    }

    public static void Postfix(object __instance)
    {
        DynamicControllerCoordinator.Tick(__instance);
    }

    public static bool ShouldPollInBackgroundForTesting(
        BrokerModeSettings settings,
        ClientControllerAssignment assignment,
        bool isFocused)
    {
        return ShouldPollInBackground(settings, assignment, isFocused);
    }

    private static bool ShouldPollInBackground(
        BrokerModeSettings settings,
        ClientControllerAssignment assignment,
        bool isFocused)
    {
        return false;
    }

}

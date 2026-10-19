using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class DynamicControllerStartupPatches
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation");
        return type is null ? null : AccessTools.Method(type, "_Ready");
    }

    public static void Postfix()
    {
        DynamicControllerCoordinator.MarkLogoReady();
    }
}

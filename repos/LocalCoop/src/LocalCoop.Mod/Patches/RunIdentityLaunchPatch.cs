using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityLaunchPatch
{
    private const string TypeName = "MegaCrit.Sts2.Core.Runs.RunManager";
    private const string MethodName = "Launch";

    public static (string TypeName, string MethodName) TargetSignatureForTesting => (TypeName, MethodName);

    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName(TypeName);
        return type is null
            ? null
            : AccessTools.GetDeclaredMethods(type).SingleOrDefault(method =>
                method.Name == MethodName && method.GetParameters().Length == 0);
    }

    [HarmonyPriority(Priority.First)]
    public static void Postfix(object? __instance)
    {
        AlignLocalContextForBrokerRunForTesting(__instance);
    }

    public static bool AlignLocalContextForBrokerRunForTesting(object? instance)
    {
        return RunIdentityAlignment.AlignBrokerRun(instance);
    }
}

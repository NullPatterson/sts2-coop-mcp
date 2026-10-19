using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class DynamicControllerInputFocusScopePatch
{
    private static readonly (string TypeName, string MethodName)[] Targets =
    [
        ("MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager", "_Input"),
        ("MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager", "_UnhandledInput"),
        ("MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager", "_UnhandledInput")
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return Targets
            .Select(target => AccessTools.Method($"{target.TypeName}:{target.MethodName}"))
            .Where(method => method is not null)
            .Cast<MethodBase>();
    }

    public static void Prefix(object[] __args, out bool __state)
    {
        EnterFocusScope(__args.FirstOrDefault(), DynamicControllerCoordinator.IsEnabled, out __state);
    }

    public static void PrefixForTesting(object? inputEvent, bool dynamicControllerEnabled, out bool state)
    {
        EnterFocusScope(inputEvent, dynamicControllerEnabled, out state);
    }

    private static void EnterFocusScope(object? inputEvent, bool dynamicControllerEnabled, out bool state)
    {
        state = ShouldEnterFocusScope(inputEvent, dynamicControllerEnabled);
        if (state)
        {
            DynamicControllerWindowFocusOverride.Enter();
        }
    }

    public static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state)
        {
            DynamicControllerWindowFocusOverride.Exit();
        }

        return __exception;
    }

    public static bool ShouldEnterFocusScopeForTesting(object? inputEvent, bool dynamicControllerEnabled = true)
    {
        return ShouldEnterFocusScope(inputEvent, dynamicControllerEnabled);
    }

    private static bool ShouldEnterFocusScope(object? inputEvent, bool dynamicControllerEnabled)
    {
        return dynamicControllerEnabled
            && SteamControllerInputSelection.IsGeneratedInputEvent(inputEvent);
    }
}

[HarmonyPatch]
public static class DynamicControllerFocusedWindowPatch
{
    public static MethodBase? TargetMethod()
    {
        return AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:IsGameFocusedWindow");
    }

    public static void Postfix(ref bool __result)
    {
        __result = ApplyFocusOverride(__result);
    }

    public static bool ApplyFocusOverrideForTesting(bool gameReportsFocused)
    {
        return ApplyFocusOverride(gameReportsFocused);
    }

    private static bool ApplyFocusOverride(bool gameReportsFocused)
    {
        return gameReportsFocused || DynamicControllerWindowFocusOverride.IsActive;
    }
}

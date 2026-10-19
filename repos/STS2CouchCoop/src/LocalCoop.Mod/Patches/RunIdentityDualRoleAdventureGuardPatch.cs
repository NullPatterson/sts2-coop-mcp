using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityDualRoleAdventureVoidGuardPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDualRoleAdventureGuardTargets.FindTargetMethods(method =>
            RunIdentityDualRoleAdventureGuardTargets.ReturnType(method) == typeof(void));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (!RunIdentityDualRoleAdventureGuard.ShouldSuppress(__instance, __args))
        {
            return true;
        }

        if (RunIdentityDiagnostics.ShouldLogDualRoleSuppressionDiagnostics(__originalMethod))
        {
            RunIdentityDiagnostics.LogBoundary("dual-role-local-self-coop-suppressed", __originalMethod, __instance, __args);
        }

        return false;
    }
}

[HarmonyPatch]
public static class RunIdentityDualRoleAdventureBoolGuardPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDualRoleAdventureGuardTargets.FindTargetMethods(method =>
            RunIdentityDualRoleAdventureGuardTargets.ReturnType(method) == typeof(bool));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(
        MethodBase __originalMethod,
        object? __instance,
        object?[] __args,
        ref bool __result)
    {
        if (!RunIdentityDualRoleAdventureGuard.ShouldSuppress(__instance, __args))
        {
            return true;
        }

        __result = false;
        if (RunIdentityDiagnostics.ShouldLogDualRoleSuppressionDiagnostics(__originalMethod))
        {
            RunIdentityDiagnostics.LogBoundary("dual-role-local-self-coop-suppressed", __originalMethod, __instance, __args);
        }

        return false;
    }
}

public static class RunIdentityDualRoleAdventureGuardTargets
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "LocalMultiControl.Scripts.Patch.LocalMultiControlPatch",
            "LocalMultiControlPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "PostfixRunManagerLaunch"
            }
        ),
        (
            "LocalMultiControl.Scripts.Runtime.LocalSelfCoopContext",
            "LocalSelfCoopContext",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "get_IsEnabled",
                "get_UseSingleAdventureMode",
                "get_UseSingleEventFlow"
            }
        ),
        (
            "LocalMultiControl.Scripts.Runtime.LocalControlSwitchGuard",
            "LocalControlSwitchGuard",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "CanSwitchNow",
                "TrySwitchNext",
                "TrySwitchPrevious",
                "TrySwitchTo"
            }
        ),
        (
            "LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime",
            "LocalMultiControlRuntime",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "OnRunLaunched",
                "SwitchNextControlledPlayer",
                "SwitchPreviousControlledPlayer",
                "SwitchControlledPlayerTo",
                "TryRunPendingEventAutoSwitch",
                "ApplyControlContext",
                "SyncRunSynchronizerLocalPlayerId",
                "AlignContextForActionOwner",
                "TryAutoSwitchAfterEndTurn",
                "CanSwitchDuringCombat",
                "TrySwitchCombatPlayer",
                "TrySwitchToNextOperableNonWakuuPlayer",
                "TrySwitchToNextPlayablePlayer",
                "RefreshCombatUiForControlledPlayer",
                "RefreshCombatEnergyUi",
                "RefreshCombatEnergyForCurrentPlayer",
                "RefreshSharedTopBarForCombat",
                "RefreshTopBarForControlledPlayer",
                "RefreshTopBarDeck",
                "RefreshDeckViewForControlledPlayer",
                "RefreshRestSiteForControlledPlayer",
                "RefreshEventRoomForControlledPlayer"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NEventRoomPatch",
            "NEventRoomPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Postfix",
                "TryAutoSwitchToNextPendingEvent"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NCombatRoomPatch",
            "NCombatRoomPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Postfix"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NCombatUiReadyPatch",
            "NCombatUiReadyPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Postfix"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NCombatUiActivatePatch",
            "NCombatUiActivatePatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Postfix"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NCombatUiEnablePatch",
            "NCombatUiEnablePatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Postfix"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.LocalCombatSwitchButtons",
            "LocalCombatSwitchButtons",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Ensure",
                "Refresh"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.LocalMultiplayerPlayerStateSwitchUi",
            "LocalMultiplayerPlayerStateSwitchUi",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Ensure",
                "Refresh",
                "TrySwitchToPlayer"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.LocalCombatSwitchTracker",
            "LocalCombatSwitchTracker",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "_Process"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.LocalPlayerStateSwitchTracker",
            "LocalPlayerStateSwitchTracker",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "_Process"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NPotionContainerPatch",
            "NPotionContainerPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "PostfixInitialize",
                "PrefixAnimatePotion"
            }
        ),
        (
            "LocalMultiControl.Scripts.Patch.NRelicInventoryPatch",
            "NRelicInventoryPatch",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Prefix"
            }
        )
    ];

    public static IReadOnlyList<(string TypeName, string MethodName)> TargetSignaturesForTesting =>
        Targets
            .SelectMany(target => target.MethodNames.Select(methodName => (target.TypeName, methodName)))
            .ToArray();

    public static IEnumerable<MethodBase> FindTargetMethods(Func<MethodBase, bool> predicate)
    {
        foreach (var (typeName, simpleName, methodNames) in Targets)
        {
            foreach (var method in RunIdentityDiagnosticsPatchTarget.FindMethods(typeName, simpleName, methodNames))
            {
                if (predicate(method))
                {
                    yield return method;
                }
            }
        }
    }

    public static Type ReturnType(MethodBase method)
    {
        return method is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
    }
}

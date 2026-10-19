using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RewardDiagnosticsPatches
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton",
            "NRewardButton",
            new HashSet<string>(StringComparer.Ordinal) { "GetReward" }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.Reward",
            "Reward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSelectWrapper" }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.PotionReward",
            "PotionReward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSelect" }
        )
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (typeName, simpleName, methodNames) in Targets)
        {
            foreach (var method in RunIdentityDiagnosticsPatchTarget.FindMethods(typeName, simpleName, methodNames))
            {
                yield return method;
            }
        }
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (IsRewardAttemptMethod(__originalMethod))
        {
            RunIdentityDiagnostics.StartCorrelation("reward-ui");
        }
        else if (IsRewardReceiveMethod(__originalMethod))
        {
            RunIdentityDiagnostics.StartCorrelation("reward-message");
        }
        else
        {
            RunIdentityDiagnostics.EnsureCorrelation("reward-flow");
        }

        RunIdentityDiagnostics.LogBoundary("reward-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args, object? __result)
    {
        if (__result is Task)
        {
            RunIdentityDiagnostics.LogTaskResultWhenComplete("reward-exit", __originalMethod, __instance, __args, __result);
            return;
        }

        RunIdentityDiagnostics.LogResult("reward-exit", __originalMethod, __instance, __args, __result);
    }

    private static bool IsRewardAttemptMethod(MethodBase method)
    {
        return string.Equals(method.DeclaringType?.Name, "NRewardButton", StringComparison.Ordinal)
            && string.Equals(method.Name, "GetReward", StringComparison.Ordinal);
    }

    private static bool IsRewardReceiveMethod(MethodBase method)
    {
        return string.Equals(method.DeclaringType?.Name, "RewardSynchronizer", StringComparison.Ordinal)
            && string.Equals(method.Name, "HandleRewardObtainedMessage", StringComparison.Ordinal);
    }
}

[HarmonyPatch]
public static class RewardStateDiagnosticsPatches
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton",
            "NRewardButton",
            new HashSet<string>(StringComparer.Ordinal) { "OnPress", "OnRelease" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen",
            "NRewardsScreen",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "RewardCollectedFrom",
                "RewardSkippedFrom",
                "SetRewards",
                "TryEnableProceedButton",
                "UpdateScreenState",
                "OnProceedButtonPressed"
            }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.Reward",
            "Reward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSkipped" }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.PotionReward",
            "PotionReward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSkipped" }
        ),
        (
            "MegaCrit.Sts2.Core.Multiplayer.Game.RewardSynchronizer",
            "RewardSynchronizer",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "HandleRewardObtainedMessage",
                "SyncLocalObtainedCard",
                "SyncLocalObtainedGold",
                "SyncLocalObtainedPotion",
                "SyncLocalObtainedRelic",
                "SyncLocalSkippedCard",
                "SyncLocalSkippedPotion",
                "SyncLocalSkippedRelic",
                "SyncLocalPotionEvent",
                "SyncLocalRelicEvent",
                "SyncLocalCardEvent"
            }
        )
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (typeName, simpleName, methodNames) in Targets)
        {
            foreach (var method in RunIdentityDiagnosticsPatchTarget.FindMethods(typeName, simpleName, methodNames))
            {
                yield return method;
            }
        }
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (IsRewardAttemptMethod(__originalMethod))
        {
            RunIdentityDiagnostics.StartCorrelation("reward-ui");
        }
        else if (IsRewardReceiveMethod(__originalMethod))
        {
            RunIdentityDiagnostics.StartCorrelation("reward-message");
        }
        else
        {
            RunIdentityDiagnostics.EnsureCorrelation("reward-flow");
        }

        RunIdentityDiagnostics.LogBoundary("reward-state-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogBoundary("reward-state-exit", __originalMethod, __instance, __args);
    }

    private static bool IsRewardAttemptMethod(MethodBase method)
    {
        return string.Equals(method.DeclaringType?.Name, "NRewardButton", StringComparison.Ordinal)
            && (string.Equals(method.Name, "OnPress", StringComparison.Ordinal)
                || string.Equals(method.Name, "OnRelease", StringComparison.Ordinal));
    }

    private static bool IsRewardReceiveMethod(MethodBase method)
    {
        return string.Equals(method.DeclaringType?.Name, "RewardSynchronizer", StringComparison.Ordinal)
            && string.Equals(method.Name, "HandleRewardObtainedMessage", StringComparison.Ordinal);
    }
}

[HarmonyPatch]
public static class PotionProcurementDiagnosticsPatches
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.Commands.PotionCmd",
            "PotionCmd",
            new HashSet<string>(StringComparer.Ordinal) { "TryToProcure" });
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.EnsureCorrelation("potion-procure");
        RunIdentityDiagnostics.LogBoundary("potion-procure-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args, object? __result)
    {
        RunIdentityDiagnostics.LogTaskResultWhenComplete(
            "potion-procure-exit",
            __originalMethod,
            __instance,
            __args,
            __result);
    }
}

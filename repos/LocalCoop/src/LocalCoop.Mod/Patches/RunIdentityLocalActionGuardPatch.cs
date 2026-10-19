using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityLocalActionGuardPatch
{
    internal static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton",
            "NEventOptionButton",
            new HashSet<string>(StringComparer.Ordinal) { "OnPress", "OnRelease" }
        ),
        (
            "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            "EventSynchronizer",
            new HashSet<string>(StringComparer.Ordinal) { "ChooseLocalOption" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton",
            "NRewardButton",
            new HashSet<string>(StringComparer.Ordinal) { "OnPress", "OnRelease" }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.Reward",
            "Reward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSelectWrapper", "OnSkipped" }
        ),
        (
            "MegaCrit.Sts2.Core.Rewards.PotionReward",
            "PotionReward",
            new HashSet<string>(StringComparer.Ordinal) { "OnSelect", "OnSkipped" }
        ),
        (
            "MegaCrit.Sts2.Core.Commands.PotionCmd",
            "PotionCmd",
            new HashSet<string>(StringComparer.Ordinal) { "TryToProcure" }
        ),
        (
            "MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer",
            "OneOffSynchronizer",
            new HashSet<string>(StringComparer.Ordinal) { "DoLocalMerchantCardRemoval" }
        )
    ];

    public static IReadOnlyList<(string TypeName, string MethodName)> TargetSignaturesForTesting =>
        Targets
            .SelectMany(target => target.MethodNames.Select(methodName => (target.TypeName, methodName)))
            .ToArray();

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return FindTargetMethods(method => ReturnType(method) == typeof(void));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        return ShouldRunOriginal(__originalMethod, __instance, __args);
    }

    internal static IEnumerable<MethodBase> FindTargetMethods(Func<MethodBase, bool> predicate)
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

    internal static bool ShouldRunOriginal(MethodBase method, object? instance, object?[] args)
    {
        var aligned = RunIdentityLocalUiAlignmentPatch.AlignLocalUiForTesting(instance);
        if (!aligned)
        {
            return true;
        }

        if (RunIdentityLocalActionGuard.ShouldAllowLocalAction(instance, args))
        {
            return true;
        }

        RunIdentityDiagnostics.LogBoundary("local-action-suppressed", method, instance, args);
        return false;
    }

    internal static Type ReturnType(MethodBase method)
    {
        return method is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
    }
}

[HarmonyPatch]
public static class RunIdentityLocalActionTaskGuardPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityLocalActionGuardPatch.FindTargetMethods(method =>
            RunIdentityLocalActionGuardPatch.ReturnType(method) == typeof(Task));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(
        MethodBase __originalMethod,
        object? __instance,
        object?[] __args,
        ref Task __result)
    {
        if (RunIdentityLocalActionGuardPatch.ShouldRunOriginal(__originalMethod, __instance, __args))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityRewardAlignmentPatch
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton",
            "NRewardButton",
            new HashSet<string>(StringComparer.Ordinal) { "OnPress", "OnRelease", "GetReward" }
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

    public static IReadOnlyList<(string TypeName, string MethodName)> TargetSignaturesForTesting =>
        Targets
            .SelectMany(target => target.MethodNames.Select(methodName => (target.TypeName, methodName)))
            .ToArray();

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

    [HarmonyPriority(Priority.Last)]
    public static void Prefix(object? __instance)
    {
        AlignRunIdentityForRewardBoundaryForTesting(__instance);
    }

    public static bool AlignRunIdentityForRewardBoundaryForTesting(object? instance)
    {
        var alignedInstance = RunIdentityAlignment.AlignBrokerRun(instance);
        var alignedRememberedRun = RunIdentityAlignment.AlignRememberedBrokerRun();
        return alignedInstance || alignedRememberedRun;
    }
}

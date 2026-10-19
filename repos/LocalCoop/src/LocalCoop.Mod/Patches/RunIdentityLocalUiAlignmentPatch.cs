using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityLocalUiAlignmentPatch
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            "EventSynchronizer",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "GetLocalEvent",
                "ChooseLocalOption"
            }
        ),
        (
            "MegaCrit.Sts2.Core.Rooms.EventRoom",
            "EventRoom",
            new HashSet<string>(StringComparer.Ordinal) { "EnterInternal" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom",
            "NEventRoom",
            new HashSet<string>(StringComparer.Ordinal) { "Create", "SetOptions" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen",
            "NRewardsScreen",
            new HashSet<string>(StringComparer.Ordinal) { "SetRewards" }
        ),
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
            "MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer",
            "OneOffSynchronizer",
            new HashSet<string>(StringComparer.Ordinal) { "DoLocalMerchantCardRemoval" }
        ),
        (
            "MegaCrit.Sts2.Core.GameActions.CardSelectCmd",
            "CardSelectCmd",
            new HashSet<string>(StringComparer.Ordinal) { "FromDeckForRemoval", "ShouldSelectLocalCard" }
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

    [HarmonyPriority(Priority.First)]
    public static void Prefix(object? __instance)
    {
        AlignLocalUiForTesting(__instance);
    }

    public static bool AlignLocalUiForTesting(object? instance)
    {
        var alignedInstance = RunIdentityAlignment.AlignBrokerRun(instance);
        var alignedRememberedRun = RunIdentityAlignment.AlignRememberedBrokerRun();
        return alignedInstance || alignedRememberedRun;
    }
}

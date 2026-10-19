using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityLocalUiDiagnosticsPatches
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            "EventSynchronizer",
            new HashSet<string>(StringComparer.Ordinal) { "GetLocalEvent", "ChooseLocalOption" }
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
            "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton",
            "NEventOptionButton",
            new HashSet<string>(StringComparer.Ordinal) { "Create", "OnPress", "OnRelease" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen",
            "NRewardsScreen",
            new HashSet<string>(StringComparer.Ordinal) { "SetRewards" }
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton",
            "NRewardButton",
            new HashSet<string>(StringComparer.Ordinal) { "Create", "GetReward", "OnPress", "OnRelease" }
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
        RunIdentityDiagnostics.EnsureCorrelation("local-ui");
        RunIdentityDiagnostics.LogLocalUiBoundary("local-ui-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogLocalUiBoundary("local-ui-exit", __originalMethod, __instance, __args);
    }
}

[HarmonyPatch]
public static class PeerInputOwnershipDiagnosticsPatches
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal)
    {
        "SyncLocalControllerFocus",
        "SyncLocalHoveredModel",
        "SyncLocalMouseDown",
        "SyncLocalMousePos",
        "SyncLocalScreen"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.PeerInputSynchronizer",
            "PeerInputSynchronizer",
            MethodNames);
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (!RunIdentityDiagnostics.ShouldLogPeerInputDiagnostics("peer-input-ownership-enter", __originalMethod))
        {
            return;
        }

        RunIdentityDiagnostics.EnsureCorrelation("peer-input");
        RunIdentityDiagnostics.LogLocalUiBoundary("peer-input-ownership-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (!RunIdentityDiagnostics.ShouldLogPeerInputDiagnostics("peer-input-ownership-exit", __originalMethod))
        {
            return;
        }

        RunIdentityDiagnostics.LogLocalUiBoundary("peer-input-ownership-exit", __originalMethod, __instance, __args);
    }
}

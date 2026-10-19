using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityPotionAnimationGuardPatch
{
    private const string MissingPotionSlotFailure = "Sequence contains no matching element";

    public static MethodBase? TargetMethod()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethod(
            "MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer",
            "NPotionContainer",
            "AnimatePotion");
    }

    public static Exception? Finalizer(
        MethodBase __originalMethod,
        object? __instance,
        object?[] __args,
        Exception? __exception)
    {
        if (__exception is null)
        {
            return null;
        }

        var filtered = FilterPotionAnimationExceptionForTesting(__exception);
        if (filtered is null)
        {
            RunIdentityDiagnostics.LogResult(
                "potion-animation-visual-fault-suppressed",
                __originalMethod,
                __instance,
                __args,
                $"{__exception.GetType().Name}: {__exception.Message}");
        }

        return filtered;
    }

    public static Exception? FilterPotionAnimationExceptionForTesting(Exception exception)
    {
        return ShouldSuppressPotionAnimationException(exception) ? null : exception;
    }

    private static bool ShouldSuppressPotionAnimationException(Exception exception)
    {
        return exception is InvalidOperationException
            && string.Equals(exception.Message, MissingPotionSlotFailure, StringComparison.Ordinal)
            && RunIdentityAlignment.AlignRememberedBrokerRun();
    }
}

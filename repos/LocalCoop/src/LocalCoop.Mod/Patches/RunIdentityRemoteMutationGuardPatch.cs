using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Models;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityRemoteMutationGuardPatch
{
    public static IReadOnlyList<MethodBase> TargetMethodsForTesting =>
        RunIdentityRemoteMutationGuardTargets.FindTargetMethods(_ => true).ToArray();

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityRemoteMutationGuardTargets.FindTargetMethods(method =>
            RunIdentityRemoteMutationGuardTargets.ReturnType(method) == typeof(void));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (RunIdentityRemoteMutationGuard.ShouldAllowNativeMutation(__instance, __args))
        {
            return true;
        }

        RunIdentityDiagnostics.LogBoundary("remote-mutation-suppressed", __originalMethod, __instance, __args);
        return false;
    }
}

[HarmonyPatch]
public static class RunIdentityRemoteMutationTaskGuardPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityRemoteMutationGuardTargets.FindTargetMethods(method =>
            RunIdentityRemoteMutationGuardTargets.ReturnType(method) == typeof(Task<RelicModel>));
    }

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(
        MethodBase __originalMethod,
        object? __instance,
        object?[] __args,
        ref Task<RelicModel> __result)
    {
        if (RunIdentityRemoteMutationGuard.ShouldAllowNativeMutation(__instance, __args))
        {
            return true;
        }

        __result = SuppressedTaskResult(__originalMethod);
        RunIdentityDiagnostics.LogBoundary("remote-mutation-suppressed", __originalMethod, __instance, __args);
        return false;
    }

    public static Task? SuppressedTaskResultForTesting(MethodBase method)
    {
        return SuppressedTaskResult(method);
    }

    private static Task<RelicModel> SuppressedTaskResult(MethodBase method)
    {
        return RunIdentityRemoteMutationGuardTargets.ReturnType(method) == typeof(Task<RelicModel>)
            ? Task.FromResult<RelicModel>(null!)
            : throw new NotSupportedException($"Unsupported remote mutation return type: {method}");
    }
}

internal static class RunIdentityRemoteMutationGuardTargets
{
    public static IEnumerable<MethodBase> FindTargetMethods(Func<MethodBase, bool> predicate)
    {
        foreach (var method in RunIdentityDiagnosticsPatchTarget.FindMethods(
                     "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
                     "EventSynchronizer",
                     new HashSet<string>(StringComparer.Ordinal) { "ChooseOptionForEvent" }))
        {
            if (predicate(method))
            {
                yield return method;
            }
        }

        foreach (var method in FindRelicObtainMethods())
        {
            if (predicate(method))
            {
                yield return method;
            }
        }
    }

    public static Type ReturnType(MethodBase method)
    {
        return method is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
    }

    private static IEnumerable<MethodBase> FindRelicObtainMethods()
    {
        var type = RunIdentityDiagnosticsPatchTarget.FindType(
            "MegaCrit.Sts2.Core.Commands.RelicCmd",
            "RelicCmd");
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method =>
                     string.Equals(method.Name, "Obtain", StringComparison.Ordinal)
                     && !method.IsAbstract
                     && !method.ContainsGenericParameters
                     && !method.IsGenericMethodDefinition
                     && IsRelicObtainForPlayer(method)))
        {
            yield return method;
        }
    }

    private static bool IsRelicObtainForPlayer(MethodBase method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 3
            && string.Equals(parameters[0].ParameterType.FullName, "MegaCrit.Sts2.Core.Models.RelicModel", StringComparison.Ordinal)
            && string.Equals(parameters[1].ParameterType.FullName, "MegaCrit.Sts2.Core.Entities.Players.Player", StringComparison.Ordinal)
            && parameters[2].ParameterType == typeof(int);
    }
}

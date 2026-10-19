using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

internal static class RunIdentityDiagnosticsPatchTarget
{
    public static Type? FindType(string fullName, string simpleName)
    {
        return AccessTools.TypeByName(fullName)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(GetLoadableTypes)
                .FirstOrDefault(type => string.Equals(type.Name, simpleName, StringComparison.Ordinal));
    }

    public static IEnumerable<MethodBase> FindMethods(string fullName, string simpleName, IReadOnlySet<string> methodNames)
    {
        var type = FindType(fullName, simpleName);
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method =>
                     methodNames.Contains(method.Name)
                     && !method.IsAbstract
                     && !method.ContainsGenericParameters
                     && !method.IsGenericMethodDefinition))
        {
            yield return method;
        }
    }

    public static MethodBase? FindMethod(string fullName, string simpleName, string methodName)
    {
        return FindMethods(fullName, simpleName, new HashSet<string>(StringComparer.Ordinal) { methodName }).SingleOrDefault();
    }

    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }
}

[HarmonyPatch]
public static class RunIdentityLifecycleDiagnosticsPatches
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal)
    {
        "InitializeShared",
        "Launch"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.Runs.RunManager",
            "RunManager",
            MethodNames);
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogBoundary("run-lifecycle-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (string.Equals(__originalMethod.Name, "InitializeShared", StringComparison.Ordinal)
            || string.Equals(__originalMethod.Name, "Launch", StringComparison.Ordinal))
        {
            RunIdentityAlignment.AlignBrokerRun(__instance);
        }

        RunIdentityDiagnostics.LogBoundary("run-lifecycle-exit", __originalMethod, __instance, __args);
    }
}

[HarmonyPatch]
public static class OneOffSynchronizerDiagnosticsPatches
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal)
    {
        "DoLocalMerchantCardRemoval",
        "HandleMerchantCardRemoval",
        "DoMerchantCardRemoval"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer",
            "OneOffSynchronizer",
            MethodNames);
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (string.Equals(__originalMethod.Name, "DoLocalMerchantCardRemoval", StringComparison.Ordinal))
        {
            RunIdentityDiagnostics.StartCorrelation("shop-remove-local");
        }
        else if (string.Equals(__originalMethod.Name, "HandleMerchantCardRemoval", StringComparison.Ordinal))
        {
            RunIdentityDiagnostics.StartCorrelation("shop-remove-remote");
        }
        else
        {
            RunIdentityDiagnostics.EnsureCorrelation("shop-remove-native");
        }

        RunIdentityDiagnostics.LogBoundary("one-off-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogBoundary("one-off-exit", __originalMethod, __instance, __args);
    }
}

[HarmonyPatch]
public static class OneOffSynchronizerLocalPlayerDiagnosticsPatch
{
    public static MethodBase? TargetMethod()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethod(
            "MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer",
            "OneOffSynchronizer",
            "get_LocalPlayer");
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args, object? __result)
    {
        RunIdentityDiagnostics.LogResult("one-off-local-player", __originalMethod, __instance, __args, __result);
    }
}

[HarmonyPatch]
public static class CardSelectCmdDiagnosticsPatches
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.GameActions.CardSelectCmd",
            "CardSelectCmd",
            new HashSet<string>(StringComparer.Ordinal) { "FromDeckForRemoval" });
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.EnsureCorrelation("card-select-remove");
        RunIdentityDiagnostics.LogBoundary("card-select-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogBoundary("card-select-exit", __originalMethod, __instance, __args);
    }
}

[HarmonyPatch]
public static class CardSelectCmdShouldSelectLocalDiagnosticsPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.GameActions.CardSelectCmd",
            "CardSelectCmd",
            new HashSet<string>(StringComparer.Ordinal) { "ShouldSelectLocalCard" });
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.EnsureCorrelation("card-select-should-select-local");
        RunIdentityDiagnostics.LogBoundary("card-select-should-select-local-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args, bool __result)
    {
        RunIdentityDiagnostics.LogResult(
            "card-select-should-select-local-exit",
            __originalMethod,
            __instance,
            __args,
            __result);
    }
}

[HarmonyPatch]
public static class LocalContextIsMeDiagnosticsPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(RunIdentityDiagnosticsPatchTarget.GetLoadableTypes)
                .FirstOrDefault(type => string.Equals(type.Name, "LocalContext", StringComparison.Ordinal));
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method =>
                     string.Equals(method.Name, "IsMe", StringComparison.Ordinal)
                     && method.GetParameters().Length == 1
                     && string.Equals(method.GetParameters()[0].ParameterType.Name, "Player", StringComparison.Ordinal)))
        {
            yield return method;
        }
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (RunIdentityDiagnostics.HasCorrelation)
        {
            RunIdentityDiagnostics.LogBoundary("local-context-is-me-enter", __originalMethod, __instance, __args);
        }
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args, bool __result)
    {
        RunIdentityDiagnostics.LogCorrelatedResult("local-context-is-me-exit", __originalMethod, __instance, __args, __result);
    }
}

[HarmonyPatch]
public static class PlayerChoiceSynchronizerDiagnosticsPatches
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal)
    {
        "ReserveChoiceId",
        "WaitForRemoteChoice",
        "SyncLocalChoice",
        "OnReceivePlayerChoice",
        "OnPlayerChoiceMessageReceived"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return RunIdentityDiagnosticsPatchTarget.FindMethods(
            "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer",
            "PlayerChoiceSynchronizer",
            MethodNames);
    }

    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.EnsureCorrelation("player-choice");
        RunIdentityDiagnostics.LogBoundary("player-choice-enter", __originalMethod, __instance, __args);
    }

    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        RunIdentityDiagnostics.LogBoundary("player-choice-exit", __originalMethod, __instance, __args);
    }
}

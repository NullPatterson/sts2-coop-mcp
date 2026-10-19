using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityRemoteEventUiGuardPatch
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom",
            "NEventRoom",
            new HashSet<string>(StringComparer.Ordinal) { "Create", "SetOptions" }
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
    public static void Prefix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (!RebindRemoteEventUi(__instance, __args))
        {
            return;
        }

        RunIdentityDiagnostics.LogBoundary("local-ui-remote-event-rebound", __originalMethod, __instance, __args);
    }

    public static bool RebindRemoteEventUiForTesting(object? instance, object?[] args)
    {
        return RebindRemoteEventUi(instance, args);
    }

    private static bool RebindRemoteEventUi(object? instance, object?[] args)
    {
        RunIdentityLocalUiAlignmentPatch.AlignLocalUiForTesting(instance);

        var localNetId = LocalContext.NetId;
        if (localNetId is null or 0 || !TryFindEventArgument(args, out var eventArgIndex, out var eventModel))
        {
            return false;
        }

        if (!RunIdentityLocalActionGuard.TryFindPlayerNetId(eventModel, [], out var ownerNetId)
            || ownerNetId is null
            || ownerNetId.Value == localNetId.Value)
        {
            return false;
        }

        if (!TryResolveRememberedLocalEvent(out var localEvent)
            || ReferenceEquals(localEvent, eventModel)
            || !RunIdentityLocalActionGuard.TryFindPlayerNetId(localEvent, [], out var localEventOwnerNetId)
            || localEventOwnerNetId is null
            || localEventOwnerNetId.Value != localNetId.Value)
        {
            return false;
        }

        args[eventArgIndex] = localEvent;
        return true;
    }

    private static bool TryFindEventArgument(object?[] args, out int index, out object eventModel)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (LooksLikeEventModel(arg))
            {
                index = i;
                eventModel = arg!;
                return true;
            }
        }

        index = -1;
        eventModel = null!;
        return false;
    }

    private static bool LooksLikeEventModel(object? value)
    {
        if (value is null)
        {
            return false;
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        return (typeName.Contains(".Models.Events.", StringComparison.Ordinal)
                || type.Name.Contains("Event", StringComparison.Ordinal))
            && !type.Name.Contains("EventOption", StringComparison.Ordinal)
            && !type.Name.Contains("Button", StringComparison.Ordinal)
            && !type.Name.Contains("Room", StringComparison.Ordinal);
    }

    private static bool TryResolveRememberedLocalEvent(out object localEvent)
    {
        if (!RunIdentityAlignment.TryGetRememberedBrokerRun(out var runManager))
        {
            localEvent = null!;
            return false;
        }

        RunIdentityAlignment.AlignBrokerRun(runManager);
        var synchronizer = TryGetMember(runManager, "EventSynchronizer");
        if (synchronizer is null)
        {
            localEvent = null!;
            return false;
        }

        try
        {
            var getLocalEvent = AccessTools.Method(synchronizer.GetType(), "GetLocalEvent");
            var result = getLocalEvent?.Invoke(synchronizer, []);
            if (result is not null)
            {
                localEvent = result;
                return true;
            }
        }
        catch
        {
            // Keep the native UI path if the local event cannot be resolved safely.
        }

        localEvent = null!;
        return false;
    }

    private static object? TryGetMember(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        try
        {
            var property = AccessTools.Property(instance.GetType(), name);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            var field = AccessTools.Field(instance.GetType(), name);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }
}

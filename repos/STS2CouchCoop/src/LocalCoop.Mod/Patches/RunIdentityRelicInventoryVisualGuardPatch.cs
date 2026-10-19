using System.Collections;
using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityRelicInventoryVisualGuardPatch
{
    private static readonly (string TypeName, string SimpleName, HashSet<string> MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory",
            "NRelicInventory",
            new HashSet<string>(StringComparer.Ordinal) { "Add", "AnimateRelic" }
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
                if (IsExpectedRelicInventoryMethod(method))
                {
                    yield return method;
                }
            }
        }
    }

    [HarmonyPriority(Priority.First)]
    public static void Prefix(object? __instance)
    {
        RunIdentityLocalUiAlignmentPatch.AlignLocalUiForTesting(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (__args.Length == 0)
        {
            return;
        }

        if (string.Equals(__originalMethod.Name, "Add", StringComparison.Ordinal)
            && __args.Length > 1
            && __args[1] is bool startsShown
            && startsShown)
        {
            return;
        }

        if (RestoreRelicIconVisibility(__instance, __args[0]))
        {
            RunIdentityDiagnostics.LogBoundary("relic-inventory-hidden-icon-restored", __originalMethod, __instance, __args);
        }
    }

    public static bool RestoreRelicIconVisibilityForTesting(object? inventory, object? relic)
    {
        return RestoreRelicIconVisibility(inventory, relic);
    }

    private static bool IsExpectedRelicInventoryMethod(MethodBase method)
    {
        var parameters = method.GetParameters();
        return string.Equals(method.Name, "Add", StringComparison.Ordinal)
            ? parameters.Length == 3
              && string.Equals(parameters[0].ParameterType.FullName, "MegaCrit.Sts2.Core.Models.RelicModel", StringComparison.Ordinal)
              && parameters[1].ParameterType == typeof(bool)
              && parameters[2].ParameterType == typeof(int)
            : string.Equals(method.Name, "AnimateRelic", StringComparison.Ordinal)
              && parameters.Length == 3
              && string.Equals(parameters[0].ParameterType.FullName, "MegaCrit.Sts2.Core.Models.RelicModel", StringComparison.Ordinal);
    }

    private static bool RestoreRelicIconVisibility(object? inventory, object? relic)
    {
        if (inventory is null || relic is null || !TryFindRelicHolder(inventory, relic, out var holder))
        {
            return false;
        }

        return TryRestoreIconAlpha(holder);
    }

    private static bool TryFindRelicHolder(object inventory, object relic, out object holder)
    {
        foreach (var candidate in EnumerateRelicNodes(inventory))
        {
            var candidateRelic = TryGetMember(candidate, "Relic") ?? TryGetMember(candidate, "_relic");
            var candidateModel = TryGetMember(candidateRelic, "Model") ?? TryGetMember(candidateRelic, "_model");
            if (ReferenceEquals(candidateModel, relic))
            {
                holder = candidate!;
                return true;
            }
        }

        holder = null!;
        return false;
    }

    private static IEnumerable<object?> EnumerateRelicNodes(object inventory)
    {
        var relicNodes = TryGetMember(inventory, "RelicNodes") ?? TryGetMember(inventory, "_relicNodes");
        if (relicNodes is not IEnumerable enumerable || relicNodes is string)
        {
            yield break;
        }

        foreach (var relicNode in enumerable)
        {
            yield return relicNode;
        }
    }

    private static bool TryRestoreIconAlpha(object holder)
    {
        var relic = TryGetMember(holder, "Relic") ?? TryGetMember(holder, "_relic");
        var icon = TryGetMember(relic, "Icon");
        if (icon is null)
        {
            return false;
        }

        var modulateProperty = AccessTools.Property(icon.GetType(), "Modulate");
        var modulate = modulateProperty?.GetValue(icon);
        if (modulate is null)
        {
            return false;
        }

        var alphaField = AccessTools.Field(modulate.GetType(), "A");
        if (alphaField?.GetValue(modulate) is not float alpha || alpha > 0.01f)
        {
            return false;
        }

        alphaField.SetValue(modulate, 1f);
        modulateProperty!.SetValue(icon, modulate);
        return true;
    }

    private static object? TryGetMember(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        try
        {
            var type = instance.GetType();
            var property = AccessTools.Property(type, name);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            var field = AccessTools.Field(type, name);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }
}

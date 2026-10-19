using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class NetServiceDiagnosticsPatches
{
    private static readonly string[] MethodNameFragments =
    [
        "SendMessage",
        "RegisterMessageHandler",
        "UnregisterMessageHandler"
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (!ShouldInspectType(type))
            {
                continue;
            }

            foreach (var method in AccessTools.GetDeclaredMethods(type)
                         .Where(ShouldPatchMethod))
            {
                yield return method;
            }
        }
    }

    public static bool ShouldInspectType(Type type)
    {
        if (type.FullName is null || !type.FullName.StartsWith("MegaCrit.Sts2.", StringComparison.Ordinal))
        {
            return false;
        }

        return type.Name.Contains("Net", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldPatchMethod(MethodBase method)
    {
        if (method.IsAbstract)
        {
            return false;
        }

        if (method.ContainsGenericParameters || method.IsGenericMethod || method.IsGenericMethodDefinition)
        {
            return false;
        }

        return MethodNameFragments.Any(fragment => method.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static void Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        var settings = LoadSettings();
        if (!settings.Enabled)
        {
            return;
        }

        new BrokerEventLog(settings.EventLogPath).Write(PassiveTransportDiagnostics.FormatNetServiceCall(
            __instance.GetType().FullName ?? __instance.GetType().Name,
            __originalMethod.Name,
            TryGetNetId(__instance),
            __args));
    }

    private static ulong? TryGetNetId(object instance)
    {
        var type = instance.GetType();
        return AccessTools.Property(type, "NetId")?.GetValue(instance) as ulong?
            ?? AccessTools.Field(type, "<NetId>k__BackingField")?.GetValue(instance) as ulong?
            ?? AccessTools.Field(type, "_netId")?.GetValue(instance) as ulong?
            ?? AccessTools.Field(type, "netId")?.GetValue(instance) as ulong?;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
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

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class SteamControllerInputSelectionPatches
{
    [ThreadStatic]
    private static int _dynamicConnectionRefreshDepth;

    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.ControllerInput.SteamControllerInputStrategy");
        return type is null ? null : AccessTools.Method(type, "UpdateControllerConnections");
    }

    public static bool Prefix(object __instance)
    {
        LocalCoopInputRouter.RememberControllerInputStrategy(__instance);
        DynamicControllerCoordinator.RememberStrategy(__instance);
        var settings = LoadSettings();
        if (DynamicControllerCoordinator.IsEnabled)
        {
            return _dynamicConnectionRefreshDepth > 0;
        }

        if (!ShouldReplaceNativeUpdateControllerConnections(settings))
        {
            return true;
        }

        LocalCoopInputRouter.ApplyControllerSelection(
            __instance,
            LocalCoopInputRouter.ResolveAssignment(settings.Config!),
            message => new BrokerEventLog(settings.EventLogPath).Write(message));
        return false;
    }

    public static bool ShouldReplaceNativeUpdateControllerConnectionsForTesting(BrokerModeSettings settings)
    {
        return ShouldReplaceNativeUpdateControllerConnections(settings);
    }

    public static bool RefreshDynamicControllerConnections(object strategy)
    {
        var method = AccessTools.Method(strategy.GetType(), "UpdateControllerConnections");
        if (method is null)
        {
            return false;
        }

        var handleField = strategy.GetType().GetField(
            "_currentControllerHandle",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var assignedHandle = handleField?.GetValue(strategy);
        try
        {
            _dynamicConnectionRefreshDepth++;
            method.Invoke(strategy, null);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            _dynamicConnectionRefreshDepth--;
            if (handleField is not null)
            {
                handleField.SetValue(strategy, assignedHandle);
            }
        }
    }

    private static bool ShouldReplaceNativeUpdateControllerConnections(BrokerModeSettings settings)
    {
        return settings.Enabled
            && settings.Config is not null;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

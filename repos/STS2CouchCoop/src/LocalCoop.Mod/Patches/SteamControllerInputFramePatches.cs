using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class SteamControllerInputFramePatches
{
    private static readonly object RetryLock = new();
    private static readonly TimeSpan SelectionRetryInterval = TimeSpan.FromSeconds(1);
    private static DateTimeOffset _nextSelectionRetryAt;

    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.ControllerInput.SteamControllerInputStrategy");
        return type is null ? null : AccessTools.Method(type, "ProcessInput");
    }

    public static bool Prefix(object __instance)
    {
        LocalCoopInputRouter.RememberControllerInputStrategy(__instance);
        DynamicControllerCoordinator.RememberStrategy(__instance);
        var settings = LoadSettings();
        if (!settings.Enabled || settings.Config is null)
        {
            return true;
        }

        if (DynamicControllerCoordinator.IsEnabled)
        {
            if (!SteamControllerInputSelectionPatches.RefreshDynamicControllerConnections(__instance))
            {
                new BrokerEventLog(settings.EventLogPath).Write(
                    "Dynamic Steam connection refresh unavailable: UpdateControllerConnections was missing or failed.");
            }

            return false;
        }

        var assignment = LocalCoopInputRouter.ResolveAssignment(settings.Config);
        if (!assignment.ControllerDevice.IsConfigured || assignment.ControllerDevice.Device is null)
        {
            return true;
        }

        if (LocalCoopInputRouter.IsSelectedControllerActive(assignment))
        {
            SteamControllerInputSelection.RefreshSelectedInputStateForFrame(__instance);
            return true;
        }

        if (TryBeginSelectionRetry(DateTimeOffset.UtcNow))
        {
            LocalCoopInputRouter.ApplyControllerSelection(
                __instance,
                assignment,
                message => new BrokerEventLog(settings.EventLogPath).Write(message));
        }

        return true;
    }

    public static bool TryBeginSelectionRetryForTesting(DateTimeOffset now)
    {
        return TryBeginSelectionRetry(now);
    }

    public static void ResetSelectionRetryForTesting()
    {
        lock (RetryLock)
        {
            _nextSelectionRetryAt = default;
        }
    }

    private static bool TryBeginSelectionRetry(DateTimeOffset now)
    {
        lock (RetryLock)
        {
            if (now < _nextSelectionRetryAt)
            {
                return false;
            }

            _nextSelectionRetryAt = now + SelectionRetryInterval;
            return true;
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

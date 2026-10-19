using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BaseLibHealthBarForecastCompatibilityPatch
{
    private const string RemovedShowsInfiniteHpMember =
        "MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_ShowsInfiniteHp()";
    private const string BaseLibForecastPatchType =
        "BaseLib.Patches.UI.HealthBarForecastPatch";

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NHealthBar");
        if (type is null)
        {
            yield break;
        }

        foreach (var methodName in new[] { "RefreshForeground", "RefreshMiddleground", "RefreshText" })
        {
            var method = AccessTools.Method(type, methodName);
            if (method is not null)
            {
                yield return method;
            }
        }
    }

    public static Exception? Finalizer(Exception? __exception)
    {
        if (__exception is null)
        {
            return null;
        }

        var settings = LoadSettings();
        if (!settings.Enabled)
        {
            return __exception;
        }

        var log = new BrokerEventLog(settings.EventLogPath);
        try
        {
            return FilterBaseLibForecastException(__exception, __exception.StackTrace, log.Write);
        }
        catch (Exception exception)
        {
            log.Write($"BaseLib health bar forecast compatibility filter failed: {exception.GetType().Name}: {exception.Message}");
            return __exception;
        }
    }

    public static Exception? FilterBaseLibForecastExceptionForTesting(
        Exception? exception,
        string? stackTrace)
    {
        return FilterBaseLibForecastException(exception, stackTrace, log: null);
    }

    private static Exception? FilterBaseLibForecastException(
        Exception? exception,
        string? stackTrace,
        Action<string>? log)
    {
        if (exception is null)
        {
            return null;
        }

        if (!IsRemovedShowsInfiniteHpForecastFault(exception, stackTrace))
        {
            return exception;
        }

        log?.Invoke("BaseLib health bar forecast compatibility: suppressed removed Creature.ShowsInfiniteHp forecast overlay fault.");
        return null;
    }

    private static bool IsRemovedShowsInfiniteHpForecastFault(Exception exception, string? stackTrace)
    {
        return exception is MissingMethodException
            && exception.Message.Contains(RemovedShowsInfiniteHpMember, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(stackTrace)
            && stackTrace.Contains(BaseLibForecastPatchType, StringComparison.Ordinal);
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

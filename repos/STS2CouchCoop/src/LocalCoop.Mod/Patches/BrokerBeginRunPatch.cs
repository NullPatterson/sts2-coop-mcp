using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerBeginRunPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby");
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name == "BeginRunForAllPlayers"))
        {
            yield return method;
        }
    }

    public static Exception? Finalizer(object __instance, Exception? __exception)
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
            return FilterBeginRunException(__exception, __instance, log.Write);
        }
        catch (Exception exception)
        {
            log.Write($"Broker begin run exception filter failed: {exception.GetType().Name}: {exception.Message}");
            return __exception;
        }
    }

    public static Exception? FilterBeginRunException(
        Exception? exception,
        object? instanceOrService,
        Action<string>? log)
    {
        if (exception is null)
        {
            return null;
        }

        if (!IsBrokerHostCloseCast(exception))
        {
            return exception;
        }

        var service = ResolveBrokerNetGameService(instanceOrService);
        if (service is null || service.Type != NetGameType.Host)
        {
            return exception;
        }

        log?.Invoke("Broker begin run: suppressed native NetHostGameService close cast for broker host.");
        return null;
    }

    private static bool IsBrokerHostCloseCast(Exception exception)
    {
        return exception is InvalidCastException
            && exception.Message.Contains(typeof(BrokerNetGameService).FullName!, StringComparison.Ordinal)
            && exception.Message.Contains("NetHostGameService", StringComparison.Ordinal);
    }

    private static BrokerNetGameService? ResolveBrokerNetGameService(object? instanceOrService)
    {
        if (instanceOrService is BrokerNetGameService service)
        {
            return service;
        }

        if (instanceOrService is null)
        {
            return null;
        }

        var type = instanceOrService.GetType();
        var netServiceProperty = type.GetProperty(
            "NetService",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (netServiceProperty?.GetValue(instanceOrService) is BrokerNetGameService propertyService)
        {
            return propertyService;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(instanceOrService) is BrokerNetGameService fieldService)
            {
                return fieldService;
            }
        }

        return null;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

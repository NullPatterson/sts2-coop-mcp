using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Runtime;

public static class RunIdentityAlignment
{
    private static readonly object RememberedBrokerRunLock = new();
    private static WeakReference<object>? _rememberedBrokerRun;

    public static readonly string[] NativeLocalPlayerSynchronizerPropertyNames =
    [
        "EventSynchronizer",
        "OneOffSynchronizer",
        "RewardSynchronizer",
        "RestSiteSynchronizer",
        "FlavorSynchronizer",
        "TreasureRoomRelicSynchronizer"
    ];

    public static bool AlignBrokerRun(object? instance)
    {
        var service = ResolveBrokerNetGameService(instance);
        if (service is null)
        {
            return false;
        }

        var expectedNetId = service.NetId;
        LocalContext.NetId = expectedNetId;

        AlignSynchronizerLocalPlayerId(instance, expectedNetId);
        foreach (var propertyName in NativeLocalPlayerSynchronizerPropertyNames)
        {
            AlignSynchronizerLocalPlayerId(TryGetProperty(instance, propertyName), expectedNetId);
        }

        RememberBrokerRun(instance, service);
        return true;
    }

    public static bool AlignRememberedBrokerRun()
    {
        object? rememberedBrokerRun;
        lock (RememberedBrokerRunLock)
        {
            if (_rememberedBrokerRun is null || !_rememberedBrokerRun.TryGetTarget(out rememberedBrokerRun))
            {
                return false;
            }
        }

        return AlignBrokerRun(rememberedBrokerRun);
    }

    internal static bool TryGetRememberedBrokerRun(out object rememberedBrokerRun)
    {
        lock (RememberedBrokerRunLock)
        {
            if (_rememberedBrokerRun is not null && _rememberedBrokerRun.TryGetTarget(out rememberedBrokerRun!))
            {
                return true;
            }
        }

        rememberedBrokerRun = null!;
        return false;
    }

    public static void ClearRememberedBrokerRunForTesting()
    {
        lock (RememberedBrokerRunLock)
        {
            _rememberedBrokerRun = null;
        }
    }

    internal static BrokerNetGameService? ResolveBrokerNetGameService(object? instance)
    {
        if (instance is BrokerNetGameService service)
        {
            return service;
        }

        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        if (AccessTools.Property(type, "NetService")?.GetValue(instance) is BrokerNetGameService propertyService)
        {
            return propertyService;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(instance) is BrokerNetGameService fieldService)
            {
                return fieldService;
            }
        }

        return null;
    }

    private static void RememberBrokerRun(object? instance, BrokerNetGameService service)
    {
        if (instance is null || ReferenceEquals(instance, service) || !HasNativeSynchronizerProperty(instance))
        {
            return;
        }

        lock (RememberedBrokerRunLock)
        {
            _rememberedBrokerRun = new WeakReference<object>(instance);
        }
    }

    private static bool HasNativeSynchronizerProperty(object instance)
    {
        return NativeLocalPlayerSynchronizerPropertyNames.Any(propertyName => TryGetProperty(instance, propertyName) is not null);
    }

    private static void AlignSynchronizerLocalPlayerId(object? synchronizer, ulong expectedNetId)
    {
        if (synchronizer is null)
        {
            return;
        }

        var localPlayerIdField = AccessTools.Field(synchronizer.GetType(), "_localPlayerId");
        if (localPlayerIdField?.FieldType != typeof(ulong))
        {
            return;
        }

        if (localPlayerIdField.GetValue(synchronizer) is not ulong previousNetId || previousNetId == expectedNetId)
        {
            return;
        }

        localPlayerIdField.SetValue(synchronizer, expectedNetId);
    }

    private static object? TryGetProperty(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        try
        {
            var property = AccessTools.Property(instance.GetType(), name);
            return property?.GetIndexParameters().Length == 0
                ? property.GetValue(instance)
                : null;
        }
        catch
        {
            return null;
        }
    }
}

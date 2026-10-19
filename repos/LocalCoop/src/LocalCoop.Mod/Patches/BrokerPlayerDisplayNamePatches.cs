using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerPlayerDisplayNamePlatformPatch
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Platform.PlatformUtil");
        return type is null
            ? null
            : AccessTools.Method(type, "GetPlayerNameRaw", [typeof(PlatformType), typeof(ulong)]);
    }

    public static bool Prefix(ulong playerId, ref string __result)
    {
        if (!TryOverrideNameForTesting(playerId, out var name))
        {
            return true;
        }

        __result = name;
        return false;
    }

    public static bool TryOverrideNameForTesting(ulong playerId, out string name)
    {
        return BrokerPlayerDisplayNames.TryGetName(playerId, out name);
    }
}

[HarmonyPatch]
public static class BrokerPlayerDisplayNameNameplatePatch
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Multiplayer.NRemoteLobbyPlayer");
        if (type is null)
        {
            yield break;
        }

        var ready = AccessTools.Method(type, "_Ready");
        if (ready is not null)
        {
            yield return ready;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name == "OnPlayerChanged"))
        {
            yield return method;
        }
    }

    public static void Postfix(object __instance, object[] __args)
    {
        RefreshNameplateForTesting(__instance, __args.FirstOrDefault());
    }

    public static bool RefreshNameplateForTesting(object? remoteLobbyPlayer, object? changedPlayer)
    {
        if (remoteLobbyPlayer is null)
        {
            return false;
        }

        var playerId = TryGetLobbyPlayerId(changedPlayer) ?? TryGetMember(remoteLobbyPlayer, "_playerId") as ulong?;
        if (playerId is null || BrokerPlayerId.ToClientIndex(playerId.Value) < 0)
        {
            return false;
        }

        var character = TryGetLobbyPlayerCharacter(changedPlayer)
            ?? TryGetMember(remoteLobbyPlayer, "_character") as CharacterModel;
        BrokerPlayerDisplayNames.RegisterCharacter(playerId.Value, character);

        if (!BrokerPlayerDisplayNames.TryGetName(playerId.Value, out var name))
        {
            return false;
        }

        var nameplate = TryGetMember(remoteLobbyPlayer, "_nameplateLabel");
        return TrySetTextAutoSize(nameplate, name);
    }

    private static ulong? TryGetLobbyPlayerId(object? candidate)
    {
        return candidate is LobbyPlayer lobbyPlayer ? lobbyPlayer.id : null;
    }

    private static CharacterModel? TryGetLobbyPlayerCharacter(object? candidate)
    {
        return candidate is LobbyPlayer lobbyPlayer ? lobbyPlayer.character : null;
    }

    private static object? TryGetMember(object source, string name)
    {
        for (var current = source.GetType(); current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, InstanceMembers);
            if (field is not null)
            {
                return field.GetValue(source);
            }

            var property = current.GetProperty(name, InstanceMembers);
            if (property is not null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static bool TrySetTextAutoSize(object? nameplate, string name)
    {
        if (nameplate is null)
        {
            return false;
        }

        var method = nameplate
            .GetType()
            .GetMethods(InstanceMembers)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "SetTextAutoSize", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
            });

        if (method is null)
        {
            return false;
        }

        method.Invoke(nameplate, [name]);
        return true;
    }
}

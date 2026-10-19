using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Runtime;

public static class RunIdentityLocalActionGuard
{
    private const int MaxSearchDepth = 5;

    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] PlayerMemberNames =
    [
        "Player",
        "player",
        "_player",
        "Owner",
        "owner",
        "_owner",
        "LocalPlayer",
        "localPlayer",
        "_localPlayer"
    ];

    private static readonly string[] OwnedModelMemberNames =
    [
        "EventOption",
        "eventOption",
        "_eventOption",
        "Option",
        "option",
        "_option",
        "Event",
        "event",
        "_event",
        "Reward",
        "reward",
        "_reward",
        "PotionReward",
        "potionReward",
        "_potionReward",
        "Model",
        "model",
        "_model",
        "Action",
        "action",
        "_action",
        "Command",
        "command",
        "_command",
        "Choice",
        "choice",
        "_choice"
    ];

    private static readonly string[] EventMemberNames =
    [
        "Event",
        "event",
        "_event",
        "EventModel",
        "eventModel",
        "_eventModel",
        "ParentEvent",
        "parentEvent",
        "_parentEvent",
        "CurrentEvent",
        "currentEvent",
        "_currentEvent"
    ];

    private static readonly string[] FinishedMemberNames =
    [
        "IsFinished",
        "isFinished",
        "_isFinished",
        "Finished",
        "finished",
        "_finished",
        "IsDone",
        "isDone",
        "_isDone",
        "Done",
        "done",
        "_done",
        "IsComplete",
        "isComplete",
        "_isComplete",
        "Complete",
        "complete",
        "_complete",
        "Completed",
        "completed",
        "_completed"
    ];

    private static readonly string[] FinishedPlayerMemberNames =
    [
        "FinishedPlayers",
        "finishedPlayers",
        "_finishedPlayers",
        "CompletedPlayers",
        "completedPlayers",
        "_completedPlayers"
    ];

    private static readonly string[] FinishedMethodNames =
    [
        "IsFinishedFor",
        "IsFinished",
        "FinishedFor",
        "HasFinished",
        "IsDoneFor",
        "IsCompleteFor",
        "IsCompletedFor"
    ];

    public static bool ShouldAllowLocalActionForTesting(object? instance, object?[] args)
    {
        return ShouldAllowLocalAction(instance, args);
    }

    public static bool TryFindPlayerNetIdForTesting(object? instance, object?[] args, out ulong? netId)
    {
        return TryFindPlayerNetId(instance, args, out netId);
    }

    internal static bool ShouldAllowLocalAction(object? instance, object?[] args)
    {
        var localNetId = LocalContext.NetId;
        if (localNetId is null or 0)
        {
            return true;
        }

        var candidates = BuildCandidates(instance, args).ToArray();
        var player = TryFindPlayer(candidates, out var playerNetId);
        if (playerNetId is not null && playerNetId.Value != localNetId.Value)
        {
            return false;
        }

        var eventModel = TryFindEvent(candidates);
        return eventModel is null || !IsFinished(eventModel, player, playerNetId);
    }

    internal static bool TryFindPlayerNetId(object? instance, object?[] args, out ulong? netId)
    {
        TryFindPlayer(BuildCandidates(instance, args), out netId);
        return netId is not null;
    }

    private static IEnumerable<object?> BuildCandidates(object? instance, object?[] args)
    {
        yield return instance;
        foreach (var arg in args)
        {
            yield return arg;
        }
    }

    private static object? TryFindPlayer(IEnumerable<object?> candidates, out ulong? netId)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            if (TryFindPlayer(candidate, visited, 0, out var player, out netId))
            {
                return player;
            }
        }

        netId = null;
        return null;
    }

    private static bool TryFindPlayer(
        object? candidate,
        HashSet<object> visited,
        int depth,
        out object? player,
        out ulong? netId)
    {
        player = null;
        netId = null;
        if (!CanInspect(candidate, visited, depth))
        {
            return false;
        }

        if (LooksLikePlayer(candidate!) && TryGetNetId(candidate, out var candidateNetId))
        {
            player = candidate;
            netId = candidateNetId;
            return true;
        }

        foreach (var memberName in PlayerMemberNames)
        {
            if (TryFindPlayer(TryGetMember(candidate, memberName), visited, depth + 1, out player, out netId))
            {
                return true;
            }
        }

        foreach (var memberName in OwnedModelMemberNames.Concat(EventMemberNames))
        {
            if (TryFindPlayer(TryGetMember(candidate, memberName), visited, depth + 1, out player, out netId))
            {
                return true;
            }
        }

        foreach (var child in EnumerateSmallCollection(candidate))
        {
            if (TryFindPlayer(child, visited, depth + 1, out player, out netId))
            {
                return true;
            }
        }

        return false;
    }

    private static object? TryFindEvent(IEnumerable<object?> candidates)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            var eventModel = TryFindEvent(candidate, visited, 0);
            if (eventModel is not null)
            {
                return eventModel;
            }
        }

        return null;
    }

    private static object? TryFindEvent(object? candidate, HashSet<object> visited, int depth)
    {
        if (!CanInspect(candidate, visited, depth))
        {
            return null;
        }

        if (LooksLikeEvent(candidate!))
        {
            return candidate;
        }

        foreach (var memberName in EventMemberNames)
        {
            var eventModel = TryFindEvent(TryGetMember(candidate, memberName), visited, depth + 1);
            if (eventModel is not null)
            {
                return eventModel;
            }
        }

        foreach (var memberName in OwnedModelMemberNames)
        {
            var eventModel = TryFindEvent(TryGetMember(candidate, memberName), visited, depth + 1);
            if (eventModel is not null)
            {
                return eventModel;
            }
        }

        foreach (var child in EnumerateSmallCollection(candidate))
        {
            var eventModel = TryFindEvent(child, visited, depth + 1);
            if (eventModel is not null)
            {
                return eventModel;
            }
        }

        return null;
    }

    private static bool IsFinished(object eventModel, object? player, ulong? playerNetId)
    {
        if (TryGetFinishedMember(eventModel, out var isFinished))
        {
            return isFinished;
        }

        if (player is not null && TryInvokeFinishedMethod(eventModel, player, out isFinished))
        {
            return isFinished;
        }

        if (playerNetId is not null && FinishedPlayersContains(eventModel, playerNetId.Value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetFinishedMember(object eventModel, out bool isFinished)
    {
        foreach (var memberName in FinishedMemberNames)
        {
            if (TryGetMember(eventModel, memberName) is bool value)
            {
                isFinished = value;
                return true;
            }
        }

        isFinished = false;
        return false;
    }

    private static bool TryInvokeFinishedMethod(object eventModel, object player, out bool isFinished)
    {
        foreach (var methodName in FinishedMethodNames)
        {
            foreach (var method in eventModel.GetType().GetMethods(InstanceMembers).Where(method =>
                         string.Equals(method.Name, methodName, StringComparison.Ordinal)
                         && method.ReturnType == typeof(bool)
                         && !method.ContainsGenericParameters
                         && !method.IsGenericMethodDefinition))
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 0)
                    {
                        isFinished = (bool)method.Invoke(eventModel, [])!;
                        return true;
                    }

                    if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(player))
                    {
                        isFinished = (bool)method.Invoke(eventModel, [player])!;
                        return true;
                    }
                }
                catch
                {
                    // Keep native selection behavior when the action shape cannot be inspected safely.
                }
            }
        }

        isFinished = false;
        return false;
    }

    private static bool FinishedPlayersContains(object eventModel, ulong playerNetId)
    {
        foreach (var memberName in FinishedPlayerMemberNames)
        {
            if (TryGetMember(eventModel, memberName) is not IEnumerable players)
            {
                continue;
            }

            foreach (var player in players)
            {
                if (TryGetNetId(player, out var netId) && netId == playerNetId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikePlayer(object candidate)
    {
        var type = candidate.GetType();
        return string.Equals(type.Name, "Player", StringComparison.Ordinal)
            || type.Name.EndsWith("Player", StringComparison.Ordinal)
            || string.Equals(type.FullName, "MegaCrit.Sts2.Core.Entities.Players.Player", StringComparison.Ordinal);
    }

    private static bool LooksLikeEvent(object candidate)
    {
        var type = candidate.GetType();
        return type.Name.Contains("Event", StringComparison.Ordinal)
            && !type.Name.Contains("EventOption", StringComparison.Ordinal)
            && !type.Name.Contains("Button", StringComparison.Ordinal)
            && HasFinishedMarker(candidate);
    }

    private static bool HasFinishedMarker(object candidate)
    {
        return FinishedMemberNames.Any(memberName => TryGetMember(candidate, memberName) is bool)
            || FinishedPlayerMemberNames.Any(memberName => TryGetMember(candidate, memberName) is not null)
            || FinishedMethodNames.Any(methodName => candidate.GetType().GetMethods(InstanceMembers).Any(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && method.ReturnType == typeof(bool)
                && method.GetParameters().Length <= 1));
    }

    private static bool TryGetNetId(object? candidate, out ulong netId)
    {
        foreach (var memberName in new[] { "NetId", "netId", "_netId", "PlayerId", "playerId", "_playerId" })
        {
            if (TryGetMember(candidate, memberName) is ulong value)
            {
                netId = value;
                return true;
            }
        }

        netId = 0;
        return false;
    }

    private static bool CanInspect(object? candidate, HashSet<object> visited, int depth)
    {
        if (candidate is null || depth > MaxSearchDepth)
        {
            return false;
        }

        var type = candidate.GetType();
        if (type.IsPrimitive || type.IsEnum || candidate is string or decimal)
        {
            return false;
        }

        return visited.Add(candidate);
    }

    private static IEnumerable<object?> EnumerateSmallCollection(object? candidate)
    {
        if (candidate is not IEnumerable enumerable || candidate is string)
        {
            yield break;
        }

        var count = 0;
        foreach (var value in enumerable)
        {
            if (count++ >= 8)
            {
                yield break;
            }

            yield return value;
        }
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

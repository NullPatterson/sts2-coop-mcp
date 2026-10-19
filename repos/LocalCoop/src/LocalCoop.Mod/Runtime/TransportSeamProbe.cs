using System.Reflection;

namespace LocalCoop.Mod.Runtime;

public static class TransportSeamProbe
{
    private static readonly BindingFlags InspectFlags =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private static readonly (string Label, Func<Type, bool> Matches, string? PreferredName)[] Targets =
    [
        ("net game service", type => type.Name.Contains("NetGameService", StringComparison.OrdinalIgnoreCase)
            && !type.Name.Contains("Host", StringComparison.OrdinalIgnoreCase), "INetGameService"),
        ("host game service", type => type.Name.Contains("NetHostGameService", StringComparison.OrdinalIgnoreCase), "NetHostGameService"),
        ("start run lobby", type => type.Name.Contains("StartRunLobby", StringComparison.OrdinalIgnoreCase), "StartRunLobby"),
        ("steam host transport", type => type.Name.Contains("SteamHost", StringComparison.OrdinalIgnoreCase), "SteamHost"),
        ("steam client transport", type => type.Name.Contains("SteamClient", StringComparison.OrdinalIgnoreCase), "SteamClient"),
        ("character select screen", type => type.Name.Contains("CharacterSelect", StringComparison.OrdinalIgnoreCase)
            && type.Name.Contains("Screen", StringComparison.OrdinalIgnoreCase), "NCharacterSelectScreen")
    ];

    private static readonly string[] InterestingMethodNames =
    [
        "SendMessage",
        "SendMessageToClient",
        "RegisterMessageHandler",
        "UnregisterMessageHandler",
        "Create",
        "Factory",
        "Lobby",
        "Receive",
        "Dispatch",
        "Handle",
        "InitializeMultiplayerAsHost",
        "InitializeMultiplayerAsClient"
    ];

    public static TransportSeamProbeResult Run(IReadOnlyList<Assembly>? assemblies = null)
    {
        var sourceAssemblies = assemblies ?? AppDomain.CurrentDomain.GetAssemblies();
        var types = sourceAssemblies.SelectMany(GetLoadableTypes).ToArray();
        var entries = new List<TransportSeamProbeEntry>();

        foreach (var (label, matches, preferredName) in Targets)
        {
            var type = types.Where(matches)
                .OrderBy(type => TargetScore(type, preferredName))
                .FirstOrDefault();
            if (type is null)
            {
                entries.Add(new TransportSeamProbeEntry(label, "missing", []));
                continue;
            }

            entries.Add(new TransportSeamProbeEntry(
                label,
                type.FullName ?? type.Name,
                DescribeInterestingMembers(type)));
        }

        return new TransportSeamProbeResult(entries);
    }

    private static int TargetScore(Type type, string? preferredName)
    {
        var score = 0;
        if (preferredName is not null && string.Equals(type.Name, preferredName, StringComparison.Ordinal))
        {
            score -= 100;
        }

        if (type.IsInterface)
        {
            score += 50;
        }

        if (type.IsAbstract)
        {
            score += 10;
        }

        return score;
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

    private static IReadOnlyList<string> DescribeInterestingMembers(Type type)
    {
        return type.GetConstructors(InspectFlags)
            .Select(FormatConstructor)
            .Concat(type.GetMethods(InspectFlags)
            .Where(method => InterestingMethodNames.Any(name => method.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(method => method.Name)
            .Select(FormatMethod))
            .ToArray();
    }

    private static string FormatConstructor(ConstructorInfo constructor)
    {
        var parameters = string.Join(", ", constructor.GetParameters().Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"));
        return $"ctor {constructor.DeclaringType?.FullName ?? constructor.DeclaringType?.Name ?? ".ctor"}({parameters})";
    }

    private static string FormatMethod(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"));
        return $"method {FormatType(method.ReturnType)} {method.Name}({parameters})";
    }

    private static string FormatType(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name.Split('`')[0];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
        }

        return type.FullName ?? type.Name;
    }
}

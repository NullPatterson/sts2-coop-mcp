namespace LocalCoop.Mod.Runtime;

public static class PassiveTransportDiagnostics
{
    public static string FormatLobbyLifecycle(string typeName, string methodName, IReadOnlyList<object?> args)
    {
        return $"Lobby lifecycle: {typeName}.{methodName} args=[{string.Join(", ", args.Select(FormatArg))}]";
    }

    public static string FormatNetServiceCall(string typeName, string methodName, ulong? netId, IReadOnlyList<object?> args)
    {
        return $"Net service: {typeName}.{methodName} netId={netId?.ToString() ?? "unknown"} args=[{string.Join(", ", args.Select(FormatArg))}]";
    }

    private static string FormatArg(object? arg)
    {
        return arg switch
        {
            null => "null",
            int value => value.ToString(),
            ulong value => value.ToString(),
            string value => value,
            _ => arg.GetType().FullName ?? arg.GetType().Name
        };
    }
}


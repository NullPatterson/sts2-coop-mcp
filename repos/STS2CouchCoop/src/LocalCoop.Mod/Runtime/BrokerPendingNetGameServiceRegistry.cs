namespace LocalCoop.Mod.Runtime;

public static class BrokerPendingNetGameServiceRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, BrokerNetGameService> ServicesByClientId = new(StringComparer.Ordinal);

    public static void Store(string clientId, BrokerNetGameService service)
    {
        lock (Gate)
        {
            if (ServicesByClientId.Remove(clientId, out var previous))
            {
                previous.Dispose();
            }

            ServicesByClientId[clientId] = service;
        }
    }

    public static bool TryTake(string clientId, out BrokerNetGameService? service)
    {
        lock (Gate)
        {
            if (!ServicesByClientId.Remove(clientId, out service))
            {
                service = null;
                return false;
            }

            return true;
        }
    }

    public static void ClearForTesting()
    {
        lock (Gate)
        {
            foreach (var service in ServicesByClientId.Values)
            {
                service.Dispose();
            }

            ServicesByClientId.Clear();
        }
    }
}

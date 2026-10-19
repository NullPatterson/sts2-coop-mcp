namespace LocalCoop.MultiClientHarness;

public static class ClientConfigFileSetup
{
    public static ClientConfigFileSetupResult Write(string rootDirectory, ClientLaunchPlan plan)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory must not be blank.", nameof(rootDirectory));
        }

        var clients = new List<ClientConfigFileEntry>();
        foreach (var (entry, index) in plan.Clients.Select((entry, index) => (entry, index)))
        {
            var clientDirectory = Path.Combine(rootDirectory, entry.ClientId);
            Directory.CreateDirectory(clientDirectory);
            File.WriteAllText(Path.Combine(clientDirectory, "enable-local-broker.txt"), entry.ConfigContent);
            clients.Add(new ClientConfigFileEntry(entry.ClientId, index, clientDirectory));
        }

        return new ClientConfigFileSetupResult(clients);
    }
}

public sealed record ClientConfigFileSetupResult(IReadOnlyList<ClientConfigFileEntry> Clients);

public sealed record ClientConfigFileEntry(string ClientId, int ClientIndex, string Directory);


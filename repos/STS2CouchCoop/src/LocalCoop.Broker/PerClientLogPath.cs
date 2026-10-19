namespace LocalCoop.Broker;

public static class PerClientLogPath
{
    public static string For(string modDirectory, BrokerClientRegistration registration)
    {
        var role = registration.Role.ToString().ToLowerInvariant();
        return Path.Combine(modDirectory, $"localcoop-{role}-{registration.ClientIndex}-events.txt");
    }
}


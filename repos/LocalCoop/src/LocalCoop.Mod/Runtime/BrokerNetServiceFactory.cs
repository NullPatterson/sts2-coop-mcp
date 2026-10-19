namespace LocalCoop.Mod.Runtime;

public static class BrokerNetServiceFactory
{
    public static BrokerBackedNetService? TryCreate(
        BrokerModeSettings settings,
        IBrokerEnvelopeTransport transport,
        Action<string>? log = null,
        BrokerClientRole? effectiveRole = null)
    {
        if (!settings.Enabled || settings.Config is null)
        {
            return null;
        }

        return new BrokerBackedNetService(
            settings.Config.SessionId,
            settings.ClientId,
            settings.Config.ClientIndex,
            transport,
            log,
            effectiveRole ?? settings.Config.Role);
    }
}

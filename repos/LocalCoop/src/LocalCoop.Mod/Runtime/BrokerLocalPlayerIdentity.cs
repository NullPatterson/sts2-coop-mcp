namespace LocalCoop.Mod.Runtime;

public static class BrokerLocalPlayerIdentity
{
    public static bool TryGetLocalPlayerId(BrokerModeSettings settings, out ulong playerId)
    {
        if (settings is { Enabled: true, Config: not null })
        {
            playerId = BrokerPlayerId.ForClientIndex(settings.Config.ClientIndex);
            return true;
        }

        playerId = 0;
        return false;
    }
}

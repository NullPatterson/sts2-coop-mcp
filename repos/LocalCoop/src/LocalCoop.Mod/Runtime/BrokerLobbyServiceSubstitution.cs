namespace LocalCoop.Mod.Runtime;

using MegaCrit.Sts2.Core.Multiplayer.Game;

public static class BrokerLobbyServiceSubstitution
{
    public static bool TrySubstituteFirstArgument(
        BrokerModeSettings settings,
        object?[] args,
        BrokerClientRole effectiveRole,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string> log)
    {
        if (!settings.Enabled || settings.Config is null || args.Length == 0)
        {
            return false;
        }

        if (effectiveRole == BrokerClientRole.Client
            && BrokerPendingNetGameServiceRegistry.TryTake(settings.ClientId, out var pendingService))
        {
            args[0] = pendingService;
            log($"Broker lobby service substituted pending client join service: clientId={settings.ClientId} configRole={settings.Config.Role} effectiveRole={effectiveRole}.");
            return true;
        }

        log($"Broker lobby service substitution connecting: clientId={settings.ClientId} configRole={settings.Config.Role} effectiveRole={effectiveRole} endpoint={settings.Config.Host}:{settings.Config.Port} sessionId={settings.Config.SessionId}.");
        var brokerService = BrokerNetServiceFactory.TryCreate(settings, createTransport(), log, effectiveRole);
        if (brokerService is null)
        {
            return false;
        }

        args[0] = new BrokerNetGameService(brokerService, ToNetGameType(effectiveRole));
        log($"Broker lobby service substituted: clientId={settings.ClientId} effectiveRole={effectiveRole} netId={brokerService.NetId}.");
        return true;
    }

    public static BrokerClientConfig CreateRegistrationConfig(
        BrokerModeSettings settings,
        BrokerClientRole effectiveRole)
    {
        var config = settings.Config ?? throw new InvalidOperationException("Broker config is missing.");
        return config with { Role = effectiveRole };
    }

    public static BrokerClientRole? ResolveRoleForLifecycle(string methodName)
    {
        return methodName switch
        {
            "InitializeMultiplayerAsHost" => BrokerClientRole.Host,
            "InitializeMultiplayerAsClient" => BrokerClientRole.Client,
            "InitializeAsHost" => BrokerClientRole.Host,
            "InitializeAsClient" => BrokerClientRole.Client,
            _ => null
        };
    }

    private static NetGameType ToNetGameType(BrokerClientRole role)
    {
        return role == BrokerClientRole.Host ? NetGameType.Host : NetGameType.Client;
    }
}

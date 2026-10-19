namespace LocalCoop.Mod.Runtime;

public sealed record BrokerClientRegistrationInfo(
    string ClientId,
    BrokerClientRole Role,
    int ClientIndex,
    int ProtocolVersion = BrokerProtocol.CurrentVersion);

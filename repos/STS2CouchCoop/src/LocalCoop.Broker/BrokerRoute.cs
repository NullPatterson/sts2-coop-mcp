using LocalCoop.Protocol;

namespace LocalCoop.Broker;

public sealed record BrokerRoute(string TargetClientId, BrokerEnvelope Envelope);


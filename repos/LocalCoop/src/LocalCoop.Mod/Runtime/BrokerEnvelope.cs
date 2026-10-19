namespace LocalCoop.Mod.Runtime;

public sealed record BrokerEnvelope(
    string SessionId,
    string SourceClientId,
    string? TargetClientId,
    string MessageType,
    byte[] Payload,
    long Sequence);

namespace LocalCoop.Mod.Runtime;

public readonly record struct BrokerConnectedPeer(ulong PeerId, bool ReadyForBroadcasting);

namespace LocalCoop.Mod.Runtime;

public interface IBrokerEnvelopeTransport
{
    IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers => [];

    event Action<BrokerClientRegistrationInfo>? PeerRegistered
    {
        add { }
        remove { }
    }

    Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken);

    Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken);
}

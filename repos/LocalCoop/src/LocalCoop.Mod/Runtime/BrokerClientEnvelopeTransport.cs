namespace LocalCoop.Mod.Runtime;

public sealed class BrokerClientEnvelopeTransport : IBrokerEnvelopeTransport
{
    private readonly BrokerSharedConnection _connection;

    public BrokerClientEnvelopeTransport(BrokerClientConnection connection)
        : this(new BrokerSharedConnection(connection))
    {
    }

    public BrokerClientEnvelopeTransport(BrokerSharedConnection connection)
    {
        _connection = connection;
    }

    public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers => _connection.ConnectedPeers;

    public event Action<BrokerClientRegistrationInfo>? PeerRegistered
    {
        add => _connection.PeerRegistered += value;
        remove => _connection.PeerRegistered -= value;
    }

    public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        return _connection.SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
    {
        return _connection.ReceiveGameplayEnvelopeAsync(cancellationToken);
    }
}

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCoop.Mod.Runtime;

public sealed class BrokerClientConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly List<BrokerClientRegistrationInfo> _connectedPeers = [];
    private readonly object _connectedPeersGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private BrokerClientConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<BrokerClientConnection> ConnectAsync(
        BrokerClientConfig config,
        string clientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must not be blank.", nameof(clientId));
        }

        var client = new TcpClient();
        await client.ConnectAsync(config.Host, config.Port, cancellationToken).ConfigureAwait(false);
        var connection = new BrokerClientConnection(client);

        await connection.WriteAsync(BrokerTransportMessage.ForRegistration(
            new BrokerClientRegistrationInfo(clientId, config.Role, config.ClientIndex)),
            cancellationToken).ConfigureAwait(false);

        var accepted = await connection.ReadTransportMessageAsync(cancellationToken).ConfigureAwait(false);
        if (accepted?.Kind == BrokerTransportMessageKind.RegistrationRejected)
        {
            await connection.DisposeAsync();
            throw new InvalidDataException(
                $"Broker registration rejected: {accepted.RegistrationRejected?.Reason ?? "unknown reason"}");
        }

        if (accepted?.Kind != BrokerTransportMessageKind.RegistrationAccepted
            || accepted.RegistrationAccepted?.ClientId != clientId
            || accepted.RegistrationAccepted.SessionId != config.SessionId
            || accepted.RegistrationAccepted.ProtocolVersion != BrokerProtocol.CurrentVersion)
        {
            await connection.DisposeAsync();
            throw new InvalidDataException("Broker did not accept registration for the requested client.");
        }

        connection.SetConnectedPeers(accepted.RegistrationAccepted.ConnectedPeers);
        return connection;
    }

    public event Action<BrokerClientRegistrationInfo>? PeerRegistered;

    public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers
    {
        get
        {
            lock (_connectedPeersGate)
            {
                return _connectedPeers.ToArray();
            }
        }
    }

    public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        return WriteAsync(BrokerTransportMessage.ForEnvelope(envelope), cancellationToken);
    }

    public async Task<BrokerEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadTransportMessageAsync(cancellationToken);
            if (message is null)
            {
                return null;
            }

            if (message.Kind == BrokerTransportMessageKind.Envelope)
            {
                return message.Envelope
                    ?? throw new InvalidDataException("Broker envelope message did not include an envelope.");
            }

            if (message.Kind == BrokerTransportMessageKind.PeerRegistered)
            {
                AddConnectedPeer(message.PeerRegistration
                    ?? throw new InvalidDataException("Broker peer registration message did not include peer data."));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetConnectedPeers(IReadOnlyList<BrokerClientRegistrationInfo> registrations)
    {
        lock (_connectedPeersGate)
        {
            _connectedPeers.Clear();
            _connectedPeers.AddRange(registrations);
        }
    }

    private void AddConnectedPeer(BrokerClientRegistrationInfo registration)
    {
        var added = false;
        lock (_connectedPeersGate)
        {
            if (_connectedPeers.All(peer => !string.Equals(peer.ClientId, registration.ClientId, StringComparison.Ordinal)))
            {
                _connectedPeers.Add(registration);
                added = true;
            }
        }

        if (added)
        {
            PeerRegistered?.Invoke(registration);
        }
    }

    private async Task WriteAsync(BrokerTransportMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            var lengthPrefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);
            await _stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<BrokerTransportMessage?> ReadTransportMessageAsync(CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        var prefixBytes = await ReadExactlyOrEndAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        if (prefixBytes == 0)
        {
            return null;
        }

        if (prefixBytes < lengthPrefix.Length)
        {
            throw new EndOfStreamException("Broker frame ended during length prefix.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0)
        {
            throw new InvalidDataException("Broker frame length must be positive.");
        }

        var payload = new byte[length];
        var payloadBytes = await ReadExactlyOrEndAsync(payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes < length)
        {
            throw new EndOfStreamException("Broker frame ended during payload.");
        }

        return JsonSerializer.Deserialize<BrokerTransportMessage>(payload, JsonOptions)
            ?? throw new InvalidDataException("Broker frame payload did not contain a message.");
    }

    private async Task<int> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private enum BrokerTransportMessageKind
    {
        Registration,
        RegistrationAccepted,
        RegistrationRejected,
        Envelope,
        PeerRegistered
    }

    private sealed record BrokerTransportMessage(
        BrokerTransportMessageKind Kind,
        BrokerClientRegistrationInfo? Registration,
        BrokerRegistrationAccepted? RegistrationAccepted,
        BrokerRegistrationRejected? RegistrationRejected,
        BrokerEnvelope? Envelope,
        BrokerClientRegistrationInfo? PeerRegistration)
    {
        public static BrokerTransportMessage ForRegistration(BrokerClientRegistrationInfo registration)
        {
            return new BrokerTransportMessage(
                BrokerTransportMessageKind.Registration,
                registration,
                RegistrationAccepted: null,
                RegistrationRejected: null,
                Envelope: null,
                PeerRegistration: null);
        }

        public static BrokerTransportMessage ForEnvelope(BrokerEnvelope envelope)
        {
            return new BrokerTransportMessage(
                BrokerTransportMessageKind.Envelope,
                Registration: null,
                RegistrationAccepted: null,
                RegistrationRejected: null,
                envelope,
                PeerRegistration: null);
        }
    }

    private sealed record BrokerRegistrationRejected(string Reason);

    private sealed record BrokerRegistrationAccepted
    {
        [JsonConstructor]
        public BrokerRegistrationAccepted(
            string clientId,
            string sessionId,
            IReadOnlyList<BrokerClientRegistrationInfo>? connectedPeers = null,
            int protocolVersion = BrokerProtocol.CurrentVersion)
        {
            ClientId = clientId;
            SessionId = sessionId;
            ConnectedPeers = connectedPeers ?? [];
            ProtocolVersion = protocolVersion;
        }

        public string ClientId { get; init; }

        public string SessionId { get; init; }

        public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers { get; init; }

        public int ProtocolVersion { get; init; }
    }
}

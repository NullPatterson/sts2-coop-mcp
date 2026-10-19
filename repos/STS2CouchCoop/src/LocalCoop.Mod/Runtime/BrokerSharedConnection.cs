using System.Threading.Channels;

namespace LocalCoop.Mod.Runtime;

public sealed class BrokerSharedConnection : IAsyncDisposable
{
    private readonly BrokerClientConnection _connection;
    private readonly Channel<BrokerEnvelope> _gameplayEnvelopes = Channel.CreateUnbounded<BrokerEnvelope>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readLoop;

    public BrokerSharedConnection(BrokerClientConnection connection)
    {
        _connection = connection;
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
    }

    public IReadOnlyList<BrokerClientRegistrationInfo> ConnectedPeers => _connection.ConnectedPeers;

    public bool IsClosed => _readLoop.IsCompleted;

    public event Action<BrokerClientRegistrationInfo>? PeerRegistered
    {
        add => _connection.PeerRegistered += value;
        remove => _connection.PeerRegistered -= value;
    }

    public event Action<BrokerEnvelope>? ControllerEnvelopeReceived;

    public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        return _connection.SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async Task<BrokerEnvelope?> ReceiveGameplayEnvelopeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _gameplayEnvelopes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await _connection.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (ControllerControlMessageTypes.IsControllerControl(envelope.MessageType))
                {
                    ControllerEnvelopeReceived?.Invoke(envelope);
                    continue;
                }

                await _gameplayEnvelopes.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            failure = exception;
        }
        finally
        {
            _gameplayEnvelopes.Writer.TryComplete(failure);
        }
    }
}

public static class BrokerSharedConnectionRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<BrokerSharedConnection>> Connections = new(StringComparer.Ordinal);

    public static Task<BrokerSharedConnection> GetOrConnectAsync(
        BrokerClientConfig config,
        string clientId,
        CancellationToken cancellationToken)
    {
        var key = $"{config.Host}:{config.Port}|{config.SessionId}|{clientId}|{config.Role}";
        lock (Gate)
        {
            if (Connections.TryGetValue(key, out var existing)
                && (existing.IsFaulted
                    || existing.IsCanceled
                    || (existing.IsCompletedSuccessfully && existing.Result.IsClosed)))
            {
                Connections.Remove(key);
            }

            if (!Connections.TryGetValue(key, out var connection))
            {
                connection = ConnectAsync(config, clientId, cancellationToken);
                Connections[key] = connection;
            }

            return connection;
        }
    }

    public static void ClearForTesting()
    {
        Task<BrokerSharedConnection>[] connections;
        lock (Gate)
        {
            connections = Connections.Values.ToArray();
            Connections.Clear();
        }

        foreach (var task in connections.Where(task => task.IsCompletedSuccessfully))
        {
            task.Result.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<BrokerSharedConnection> ConnectAsync(
        BrokerClientConfig config,
        string clientId,
        CancellationToken cancellationToken)
    {
        var connection = await BrokerClientConnection.ConnectAsync(config, clientId, cancellationToken)
            .ConfigureAwait(false);
        return new BrokerSharedConnection(connection);
    }
}

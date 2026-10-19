using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace LocalCoop.Mod.Runtime;

public sealed class BrokerBackedNetService
{
    private readonly string _sessionId;
    private readonly string _clientId;
    private readonly int _clientIndex;
    private readonly IBrokerEnvelopeTransport _transport;
    private readonly Action<string>? _log;
    private readonly BrokerClientRole? _role;
    private readonly Dictionary<string, List<Delegate>> _handlersByMessageType = new(StringComparer.Ordinal);
    private readonly List<BrokerEnvelope> _inboundQueue = [];
    private readonly List<BrokerEnvelope> _bufferedInboundQueue = [];
    private readonly object _inboundGate = new();
    private readonly Dictionary<ulong, bool> _knownPeersById = [];
    private readonly object _knownPeerGate = new();
    private readonly List<BrokerClientRegistrationInfo> _pendingPeerRegistrations = [];
    private readonly object _pendingPeerRegistrationGate = new();
    private long _sequence;

    public BrokerBackedNetService(
        string sessionId,
        string clientId,
        int clientIndex,
        IBrokerEnvelopeTransport transport,
        Action<string>? log = null,
        BrokerClientRole? role = null)
    {
        _sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session id must not be blank.", nameof(sessionId))
            : sessionId;
        _clientId = string.IsNullOrWhiteSpace(clientId)
            ? throw new ArgumentException("Client id must not be blank.", nameof(clientId))
            : clientId;
        _clientIndex = clientIndex;
        NetId = BrokerPlayerId.ForClientIndex(clientIndex);
        _transport = transport;
        _log = log;
        _role = role;

        foreach (var peer in transport.ConnectedPeers)
        {
            AddKnownPeer(BrokerPlayerId.ForClientIndex(peer.ClientIndex));
        }

        _transport.PeerRegistered += QueuePeerRegistration;
    }

    public event Action<ulong>? PeerTracked;

    public string ClientId => _clientId;

    public ulong NetId { get; }

    public bool IsConnected { get; private set; } = true;

    public bool IsGameLoading { get; private set; }

    public bool IsBufferingMessages { get; private set; }

    public IReadOnlyList<ulong> ConnectedPeerIds
    {
        get
        {
            lock (_knownPeerGate)
            {
                return _knownPeersById.Keys.Order().ToArray();
            }
        }
    }

    public IReadOnlyList<BrokerConnectedPeer> ConnectedPeers
    {
        get
        {
            lock (_knownPeerGate)
            {
                return _knownPeersById
                    .OrderBy(peer => peer.Key)
                    .Select(peer => new BrokerConnectedPeer(peer.Key, peer.Value))
                    .ToArray();
            }
        }
    }

    public void RegisterMessageHandler<T>(Action<T> handler)
    {
        RegisterHandler(MessageTypeKey<T>(), handler);
    }

    public void RegisterMessageHandler<T>(Action<T, ulong> handler)
    {
        RegisterHandler(MessageTypeKey<T>(), handler);
    }

    public void UnregisterMessageHandler<T>(Action<T> handler)
    {
        UnregisterHandler(MessageTypeKey<T>(), handler);
    }

    public void UnregisterMessageHandler<T>(Action<T, ulong> handler)
    {
        UnregisterHandler(MessageTypeKey<T>(), handler);
    }

    public void Disconnect()
    {
        IsConnected = false;
    }

    public async Task SendMessageAsync<T>(T message, ulong? targetPlayerId, CancellationToken cancellationToken)
    {
        var targetClientId = targetPlayerId is null ? null : PlayerIdToClientId(targetPlayerId.Value);
        var envelope = BrokerEnvelopeMessageSerializer.ToEnvelope(
            _sessionId,
            _clientId,
            targetClientId,
            message,
            Interlocked.Increment(ref _sequence));
        TrackKnownPeers(envelope);
        _log?.Invoke($"Broker outbound: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");
        await _transport.SendEnvelopeAsync(envelope, cancellationToken);
    }

    public void SendMessage<T>(T message, ulong playerId)
    {
        SendMessageAsync(message, playerId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void SendMessage<T>(T message)
    {
        SendMessageAsync(message, targetPlayerId: null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await _transport.ReceiveEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                EnqueueInboundEnvelope(envelope);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsConnected = false;
        }
    }

    public void Update()
    {
        foreach (var peer in DrainPendingPeerRegistrations())
        {
            AddKnownPeer(BrokerPlayerId.ForClientIndex(peer.ClientIndex));
        }

        foreach (var envelope in DrainDispatchableInboundEnvelopes())
        {
            try
            {
                _log?.Invoke($"Broker inbound flushed: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");
                var handlerCount = DispatchEnvelopeAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
                _log?.Invoke($"Broker inbound dispatched: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence} handlerCount={handlerCount}.");
            }
            catch (Exception exception)
            {
                _log?.Invoke($"Broker inbound dispatch failed: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    public void SetGameLoading(bool isGameLoading)
    {
        IsGameLoading = isGameLoading;
        _log?.Invoke($"Broker game loading changed: sessionId={_sessionId} client={_clientId} isGameLoading={isGameLoading}.");
    }

    public void SetBufferMessages(bool bufferMessages)
    {
        lock (_inboundGate)
        {
            IsBufferingMessages = bufferMessages;
            if (!bufferMessages && _bufferedInboundQueue.Count > 0)
            {
                _inboundQueue.InsertRange(0, _bufferedInboundQueue);
                _bufferedInboundQueue.Clear();
            }
        }

        _log?.Invoke($"Broker message buffering changed: sessionId={_sessionId} client={_clientId} bufferMessages={bufferMessages}.");
    }

    public void SetPeerReadyForBroadcasting(ulong peerId)
    {
        if (peerId == 0 || peerId == NetId)
        {
            return;
        }

        var added = false;
        lock (_knownPeerGate)
        {
            added = !_knownPeersById.ContainsKey(peerId);
            _knownPeersById[peerId] = true;
        }

        if (added)
        {
            _log?.Invoke($"Broker peer tracked: sessionId={_sessionId} peerId={peerId}.");
            PeerTracked?.Invoke(peerId);
        }

        _log?.Invoke($"Broker peer ready for broadcasting: sessionId={_sessionId} peerId={peerId}.");
    }

    public string GetRawLobbyIdentifier()
    {
        return _sessionId;
    }

    public async Task<int> DispatchEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TrackKnownPeers(envelope);

        _log?.Invoke($"Broker inbound: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");

        if (!_handlersByMessageType.TryGetValue(envelope.MessageType, out var handlers))
        {
            return 0;
        }

        var dispatchHandlers = handlers.ToArray();
        var firstParameterType = dispatchHandlers[0].Method.GetParameters()[0].ParameterType;
        var firstMessage = BrokerEnvelopeMessageSerializer.Deserialize(envelope, firstParameterType);
        await RebroadcastInboundClientMessageIfNeededAsync(envelope, firstMessage, cancellationToken);

        foreach (var handler in dispatchHandlers)
        {
            var parameters = handler.Method.GetParameters();
            var parameterType = parameters[0].ParameterType;
            var message = parameterType == firstParameterType
                ? firstMessage
                : BrokerEnvelopeMessageSerializer.Deserialize(envelope, parameterType);
            if (parameters.Length == 1)
            {
                InvokeHandler(handler, message);
            }
            else
            {
                InvokeHandler(handler, message, ClientIdToPlayerId(envelope.SourceClientId));
            }
        }

        await Task.CompletedTask;
        return dispatchHandlers.Length;
    }

    private async Task RebroadcastInboundClientMessageIfNeededAsync(
        BrokerEnvelope envelope,
        object message,
        CancellationToken cancellationToken)
    {
        if (_role != BrokerClientRole.Host
            || envelope.TargetClientId is not null
            || string.Equals(envelope.SourceClientId, _clientId, StringComparison.Ordinal)
            || message is not INetMessage { ShouldBroadcast: true })
        {
            return;
        }

        foreach (var targetClientId in GetReadyPeerClientIdsExcept(envelope.SourceClientId))
        {
            var forwarded = envelope with { TargetClientId = targetClientId };
            _log?.Invoke($"Broker host rebroadcast: sessionId={forwarded.SessionId} source={forwarded.SourceClientId} target={forwarded.TargetClientId} messageType={forwarded.MessageType} sequence={forwarded.Sequence}.");
            await _transport.SendEnvelopeAsync(forwarded, cancellationToken);
        }
    }

    private void RegisterHandler(string key, Delegate handler)
    {
        if (!_handlersByMessageType.TryGetValue(key, out var handlers))
        {
            handlers = [];
            _handlersByMessageType[key] = handlers;
        }

        handlers.Add(handler);
        _log?.Invoke($"Broker handler registered: sessionId={_sessionId} messageType={key} handler={FormatHandler(handler)} handlerCount={handlers.Count}.");
    }

    private void UnregisterHandler(string key, Delegate handler)
    {
        if (!_handlersByMessageType.TryGetValue(key, out var handlers))
        {
            return;
        }

        handlers.Remove(handler);
        _log?.Invoke($"Broker handler unregistered: sessionId={_sessionId} messageType={key} handler={FormatHandler(handler)} handlerCount={handlers.Count}.");
        if (handlers.Count == 0)
        {
            _handlersByMessageType.Remove(key);
        }
    }

    private void EnqueueInboundEnvelope(BrokerEnvelope envelope)
    {
        lock (_inboundGate)
        {
            _inboundQueue.Add(envelope);
        }

        _log?.Invoke($"Broker inbound queued: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");
    }

    private void QueuePeerRegistration(BrokerClientRegistrationInfo registration)
    {
        lock (_pendingPeerRegistrationGate)
        {
            _pendingPeerRegistrations.Add(registration);
        }

        _log?.Invoke($"Broker peer registration queued: sessionId={_sessionId} clientId={registration.ClientId} clientIndex={registration.ClientIndex}.");
    }

    private BrokerClientRegistrationInfo[] DrainPendingPeerRegistrations()
    {
        lock (_pendingPeerRegistrationGate)
        {
            if (_pendingPeerRegistrations.Count == 0)
            {
                return [];
            }

            var registrations = _pendingPeerRegistrations.ToArray();
            _pendingPeerRegistrations.Clear();
            return registrations;
        }
    }

    private BrokerEnvelope[] DrainDispatchableInboundEnvelopes()
    {
        lock (_inboundGate)
        {
            if (_inboundQueue.Count == 0)
            {
                return [];
            }

            var dispatchable = new List<BrokerEnvelope>();
            var remaining = new List<BrokerEnvelope>();
            var blockedRoutes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var envelope in _inboundQueue)
            {
                var routeKey = $"{envelope.SourceClientId}>{envelope.TargetClientId ?? "*"}";
                if (blockedRoutes.Contains(routeKey) || !_handlersByMessageType.TryGetValue(envelope.MessageType, out var handlers))
                {
                    remaining.Add(envelope);
                    blockedRoutes.Add(routeKey);
                    continue;
                }

                if (ShouldBufferInboundEnvelope(envelope, handlers))
                {
                    _bufferedInboundQueue.Add(envelope);
                    _log?.Invoke($"Broker inbound buffered: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");
                    continue;
                }

                dispatchable.Add(envelope);
            }

            _inboundQueue.Clear();
            _inboundQueue.AddRange(remaining);
            return dispatchable.ToArray();
        }
    }

    private bool ShouldBufferInboundEnvelope(BrokerEnvelope envelope, IReadOnlyList<Delegate> handlers)
    {
        if (!IsBufferingMessages || handlers.Count == 0)
        {
            return false;
        }

        var parameters = handlers[0].Method.GetParameters();
        if (parameters.Length == 0)
        {
            return false;
        }

        try
        {
            var message = BrokerEnvelopeMessageSerializer.Deserialize(envelope, parameters[0].ParameterType);
            return message is INetMessage { ShouldBuffer: true };
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Broker inbound buffer check failed: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private void TrackKnownPeers(BrokerEnvelope envelope)
    {
        AddKnownPeer(ClientIdToPlayerId(envelope.SourceClientId));
        if (envelope.TargetClientId is not null)
        {
            AddKnownPeer(ClientIdToPlayerId(envelope.TargetClientId));
        }
    }

    private void AddKnownPeer(ulong peerId)
    {
        if (peerId == 0 || peerId == NetId)
        {
            return;
        }

        var added = false;
        lock (_knownPeerGate)
        {
            if (!_knownPeersById.ContainsKey(peerId))
            {
                _knownPeersById.Add(peerId, false);
                added = true;
            }
        }

        if (!added)
        {
            return;
        }

        _log?.Invoke($"Broker peer tracked: sessionId={_sessionId} peerId={peerId}.");
        PeerTracked?.Invoke(peerId);
    }

    private string[] GetReadyPeerClientIdsExcept(string sourceClientId)
    {
        var sourcePeerId = ClientIdToPlayerId(sourceClientId);
        lock (_knownPeerGate)
        {
            return _knownPeersById
                .Where(peer => peer.Value && peer.Key != sourcePeerId && peer.Key != NetId)
                .OrderBy(peer => peer.Key)
                .Select(peer => PlayerIdToClientId(peer.Key))
                .ToArray();
        }
    }

    private void InvokeHandler(Delegate handler, params object?[] args)
    {
        try
        {
            handler.DynamicInvoke(args);
        }
        catch (Exception exception)
        {
            var actualException = exception.InnerException ?? exception;
            _log?.Invoke($"Broker inbound handler failed: handler={handler.Method.DeclaringType?.FullName}.{handler.Method.Name}: {actualException.GetType().Name}: {actualException.Message}");
        }
    }

    private static string MessageTypeKey<T>()
    {
        return typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;
    }

    private static string FormatHandler(Delegate handler)
    {
        return $"{handler.Method.DeclaringType?.FullName ?? "<unknown>"}.{handler.Method.Name}";
    }

    private static string PlayerIdToClientId(ulong playerId)
    {
        var clientIndex = BrokerPlayerId.ToClientIndex(playerId);
        if (clientIndex < 0)
        {
            throw new InvalidOperationException($"Player id {playerId} is not a broker player id.");
        }

        return $"client-{clientIndex}";
    }

    private static ulong ClientIdToPlayerId(string clientId)
    {
        const string prefix = "client-";
        return clientId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(clientId[prefix.Length..], out var clientIndex)
            ? BrokerPlayerId.ForClientIndex(clientIndex)
            : 0;
    }
}

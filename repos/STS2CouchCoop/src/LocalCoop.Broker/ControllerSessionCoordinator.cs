using LocalCoop.Protocol;

namespace LocalCoop.Broker;

public sealed class ControllerSessionCoordinator
{
    private static readonly TimeSpan InventoryStabilityWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CollectorLeaseTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FocusStabilityWindow = TimeSpan.FromMilliseconds(200);

    private readonly string _sessionId;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, CollectorState> _collectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControllerSourceDescriptor> _inventory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerControllerAssignmentState> _assignments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastControllerSequences = new(StringComparer.Ordinal);
    private string? _collectorClientId;
    private long _leaseGeneration;
    private long _assignmentRevision;
    private long _outboundSequence;
    private string? _pendingInventorySignature;
    private DateTimeOffset _pendingInventorySince;
    private long _lastInventorySequence;

    public ControllerSessionCoordinator(string sessionId, Action<string>? log = null)
    {
        _sessionId = sessionId;
        _log = log;
    }

    public string? CollectorClientId => _collectorClientId;

    public long LeaseGeneration => _leaseGeneration;

    public long AssignmentRevision => _assignmentRevision;

    public IReadOnlyList<BrokerControllerAssignmentState> Assignments =>
        _assignments.Values.OrderBy(assignment => assignment.PlayerSlot).ToArray();

    public IReadOnlyList<BrokerRoute> Handle(
        BrokerEnvelope envelope,
        IReadOnlyList<BrokerClientRegistration> clients,
        DateTimeOffset now)
    {
        ExpireCollectorIfNeeded(now);
        return envelope.MessageType switch
        {
            ControllerControlMessageTypes.CollectorStatus =>
                HandleCollectorStatus(envelope, clients, now),
            ControllerControlMessageTypes.InventorySnapshot =>
                HandleInventory(envelope, clients, now),
            ControllerControlMessageTypes.InputBatch =>
                HandleInput(envelope, clients),
            _ => []
        };
    }

    public IReadOnlyList<BrokerRoute> ClientDisconnected(
        string clientId,
        IReadOnlyList<BrokerClientRegistration> clients,
        DateTimeOffset now)
    {
        _collectors.Remove(clientId);
        if (!string.Equals(_collectorClientId, clientId, StringComparison.Ordinal))
        {
            return [];
        }

        _collectorClientId = null;
        _pendingInventorySignature = null;
        _lastInventorySequence = 0;
        return ElectCollector(clients, now, "collector disconnected");
    }

    private IReadOnlyList<BrokerRoute> HandleCollectorStatus(
        BrokerEnvelope envelope,
        IReadOnlyList<BrokerClientRegistration> clients,
        DateTimeOffset now)
    {
        var status = ControllerControlMessageSerializer.Deserialize<CollectorStatusMessage>(envelope);
        var isNewClient = !_collectors.TryGetValue(envelope.SourceClientId, out var previous);
        if (isNewClient
            || previous is null
            || previous.Status.WindowFocused != status.WindowFocused
            || previous.Status.SteamReady != status.SteamReady
            || previous.Status.LogoReady != status.LogoReady
            || previous.Status.ControllerEnabled != status.ControllerEnabled
            || previous.Status.ActionDataReady != status.ActionDataReady)
        {
            _log?.Invoke(
                $"Controller collector status: client={envelope.SourceClientId} index={status.ClientIndex} focused={status.WindowFocused} steamReady={status.SteamReady} actionReady={status.ActionDataReady} actions={status.ActiveDigitalActionCount}/{status.DigitalActionQueryCount}+{status.ActiveAnalogActionCount}/{status.AnalogActionQueryCount} logoReady={status.LogoReady} controllerEnabled={status.ControllerEnabled}.");
        }

        var focusedSince = status.WindowFocused
            ? previous is { Status.WindowFocused: true }
                ? previous.FocusedSince
                : now
            : null;
        _collectors[envelope.SourceClientId] = new CollectorState(status, now, focusedSince);

        var elected = ElectCollector(clients, now, "collector status changed");
        if (elected.Count > 0)
        {
            return _assignments.Count == 0
                ? elected
                : elected.Concat(RoutesToAll(clients, CreateAssignmentEnvelope())).ToArray();
        }

        if (!isNewClient)
        {
            return [];
        }

        var routes = new List<BrokerRoute>
        {
            RouteTo(envelope.SourceClientId, CreateLeaseEnvelope("collector unchanged"))
        };
        if (_assignments.Count > 0)
        {
            routes.Add(RouteTo(envelope.SourceClientId, CreateAssignmentEnvelope()));
        }

        return routes;
    }

    private IReadOnlyList<BrokerRoute> ElectCollector(
        IReadOnlyList<BrokerClientRegistration> clients,
        DateTimeOffset now,
        string reason)
    {
        var eligible = _collectors
            .Where(pair => pair.Value.Status.ControllerEnabled
                && pair.Value.Status.LogoReady
                && pair.Value.Status.SteamReady
                && now - pair.Value.LastSeen <= CollectorLeaseTimeout)
            .ToArray();
        var currentIsEligible = _collectorClientId is not null
            && eligible.Any(pair => string.Equals(pair.Key, _collectorClientId, StringComparison.Ordinal));
        string? next;
        if (_leaseGeneration == 0 && _collectorClientId is null)
        {
            var expectedCount = _collectors.Values
                .Select(state => state.Status.ControllerClientCount)
                .DefaultIfEmpty(0)
                .Max();
            var readyClientCount = eligible
                .Select(pair => pair.Value.Status.ClientIndex)
                .Distinct()
                .Count();
            if (readyClientCount < expectedCount)
            {
                return [];
            }

            next = eligible
                .OrderBy(pair => pair.Value.Status.ClientIndex)
                .Select(pair => pair.Key)
                .FirstOrDefault();
        }
        else if (currentIsEligible)
        {
            var currentCandidate = eligible.Single(pair => string.Equals(pair.Key, _collectorClientId, StringComparison.Ordinal));
            if (currentCandidate.Value.Status.WindowFocused)
            {
                return [];
            }

            next = eligible
                .Where(pair => !string.Equals(pair.Key, _collectorClientId, StringComparison.Ordinal)
                    && pair.Value.Status.WindowFocused
                    && pair.Value.Status.ActionDataReady
                    && pair.Value.FocusedSince is not null
                    && now - pair.Value.FocusedSince >= FocusStabilityWindow)
                .OrderBy(pair => pair.Value.FocusedSince)
                .ThenBy(pair => pair.Value.Status.ClientIndex)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (next is null)
            {
                return [];
            }
        }
        else
        {
            next = eligible
                .Where(pair => pair.Value.Status.WindowFocused && pair.Value.Status.ActionDataReady)
                .OrderByDescending(pair => pair.Value.FocusedSince)
                .ThenBy(pair => pair.Value.Status.ClientIndex)
                .Select(pair => pair.Key)
                .FirstOrDefault()
                ?? eligible.OrderBy(pair => pair.Value.Status.ClientIndex).Select(pair => pair.Key).FirstOrDefault();
        }

        if (next is null && _collectorClientId is not null
            && _collectors.TryGetValue(_collectorClientId, out var current)
            && now - current.LastSeen <= CollectorLeaseTimeout)
        {
            next = _collectorClientId;
        }

        if (string.Equals(next, _collectorClientId, StringComparison.Ordinal))
        {
            return [];
        }

        var previous = _collectorClientId;
        _collectorClientId = next;
        _leaseGeneration++;
        _pendingInventorySignature = null;
        _lastInventorySequence = 0;
        _lastControllerSequences.Clear();
        _log?.Invoke(
            $"Controller collector lease changed: previous={previous ?? "<none>"} current={next ?? "<none>"} generation={_leaseGeneration} reason={reason}.");
        return RoutesToAll(clients, CreateLeaseEnvelope(reason));
    }

    private void ExpireCollectorIfNeeded(DateTimeOffset now)
    {
        if (_collectorClientId is null
            || !_collectors.TryGetValue(_collectorClientId, out var current)
            || now - current.LastSeen <= CollectorLeaseTimeout)
        {
            return;
        }

        _log?.Invoke($"Controller collector lease expired: client={_collectorClientId} generation={_leaseGeneration}.");
        _collectorClientId = null;
        _leaseGeneration++;
        _pendingInventorySignature = null;
        _lastInventorySequence = 0;
        _lastControllerSequences.Clear();
    }

    private IReadOnlyList<BrokerRoute> HandleInventory(
        BrokerEnvelope envelope,
        IReadOnlyList<BrokerClientRegistration> clients,
        DateTimeOffset now)
    {
        var snapshot = ControllerControlMessageSerializer.Deserialize<ControllerInventorySnapshotMessage>(envelope);
        if (!IsCurrentCollector(envelope.SourceClientId, snapshot.LeaseGeneration, out var rejection))
        {
            _log?.Invoke($"Controller inventory dropped: source={envelope.SourceClientId} reason={rejection}.");
            return [];
        }

        if (snapshot.InventorySequence <= _lastInventorySequence)
        {
            _log?.Invoke(
                $"Controller inventory dropped: source={envelope.SourceClientId} lease={snapshot.LeaseGeneration} sequence={snapshot.InventorySequence} last={_lastInventorySequence} reason=non-monotonic sequence.");
            return [];
        }

        _lastInventorySequence = snapshot.InventorySequence;

        var unique = snapshot.Controllers
            .Where(controller => !string.IsNullOrWhiteSpace(controller.SourceId))
            .GroupBy(controller => controller.SourceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var signature = string.Join(
            "|",
            unique.Select(controller => $"{controller.SourceId}:{controller.SourceKind}:{controller.SteamHandle}").Order(StringComparer.Ordinal));
        if (!string.Equals(signature, _pendingInventorySignature, StringComparison.Ordinal))
        {
            _pendingInventorySignature = signature;
            _pendingInventorySince = now;
            _log?.Invoke($"Controller inventory observed: collector={envelope.SourceClientId} count={unique.Length} signature={signature} waitingForStability=true.");
            return [];
        }

        if (now - _pendingInventorySince < InventoryStabilityWindow)
        {
            return [];
        }

        var changed = ApplyStableInventory(unique, clients);
        if (!changed)
        {
            return [];
        }

        _assignmentRevision++;
        var assignmentEnvelope = CreateAssignmentEnvelope();
        _log?.Invoke(
            $"Controller assignments changed: revision={_assignmentRevision} assignments=[{string.Join(",", Assignments.Select(assignment => $"{assignment.SourceId}->slot{assignment.PlayerSlot}"))}].");
        return RoutesToAll(clients, assignmentEnvelope);
    }

    private bool ApplyStableInventory(
        IReadOnlyList<ControllerSourceDescriptor> controllers,
        IReadOnlyList<BrokerClientRegistration> clients)
    {
        var enabledSlots = ResolveEnabledPlayerSlots();
        if (enabledSlots is null)
        {
            _log?.Invoke("Controller assignment waiting: not all controller-enabled clients have reported status.");
            return false;
        }

        var before = string.Join("|", Assignments.Select(FormatAssignmentIdentity));
        var connectedIds = controllers.Select(controller => controller.SourceId).ToHashSet(StringComparer.Ordinal);
        var enabledSlotSet = enabledSlots.ToHashSet();
        foreach (var disconnected in _assignments
            .Where(pair => !connectedIds.Contains(pair.Key) || !enabledSlotSet.Contains(pair.Value.PlayerSlot))
            .Select(pair => pair.Key)
            .ToArray())
        {
            _assignments.Remove(disconnected);
            _lastControllerSequences.Remove(disconnected);
        }

        _inventory.Clear();
        foreach (var controller in controllers)
        {
            _inventory[controller.SourceId] = controller;
            if (_assignments.TryGetValue(controller.SourceId, out var assignment))
            {
                _assignments[controller.SourceId] = assignment with
                {
                    SteamHandle = controller.SteamHandle,
                    ControllerType = controller.ControllerType,
                    ConnectionState = assignment.ConnectionState == ControllerConnectionState.Ready
                        ? ControllerConnectionState.Ready
                        : controller.ConnectionState
                };
            }
        }

        var clientBySlot = clients.ToDictionary(client => client.ClientIndex);
        var occupied = _assignments.Values.Select(assignment => assignment.PlayerSlot).ToHashSet();
        var vacantSlots = enabledSlots
            .Where(slot => !occupied.Contains(slot))
            .ToQueue();

        foreach (var controller in controllers.Where(controller => !_assignments.ContainsKey(controller.SourceId)))
        {
            if (!vacantSlots.TryDequeue(out var slot))
            {
                break;
            }

            var targetClientId = clientBySlot.TryGetValue(slot, out var client)
                ? client.ClientId
                : $"client-{slot}";
            _assignments[controller.SourceId] = new BrokerControllerAssignmentState(
                controller.SourceId,
                controller.SourceKind,
                controller.SteamHandle,
                controller.ControllerType,
                slot,
                targetClientId,
                controller.ConnectionState);
        }

        var after = string.Join("|", Assignments.Select(FormatAssignmentIdentity));
        return !string.Equals(before, after, StringComparison.Ordinal);
    }

    private int[]? ResolveEnabledPlayerSlots()
    {
        var expectedCount = _collectors.Values
            .Select(state => state.Status.ControllerClientCount)
            .DefaultIfEmpty(0)
            .Max();
        var enabledSlots = _collectors.Values
            .Where(state => state.Status.ControllerEnabled)
            .Select(state => state.Status.ClientIndex)
            .Distinct()
            .Order()
            .ToArray();
        return enabledSlots.Length >= expectedCount
            ? enabledSlots.Take(expectedCount).ToArray()
            : null;
    }

    private IReadOnlyList<BrokerRoute> HandleInput(
        BrokerEnvelope envelope,
        IReadOnlyList<BrokerClientRegistration> clients)
    {
        var batch = ControllerControlMessageSerializer.Deserialize<ControllerInputBatchMessage>(envelope);
        if (!IsCurrentCollector(envelope.SourceClientId, batch.LeaseGeneration, out var rejection))
        {
            LogDroppedBatch(envelope.SourceClientId, batch, rejection);
            return [];
        }

        if (batch.AssignmentRevision != _assignmentRevision)
        {
            LogDroppedBatch(envelope.SourceClientId, batch, $"assignment revision {batch.AssignmentRevision} is stale; current={_assignmentRevision}");
            return [];
        }

        var routes = new List<BrokerRoute>();
        foreach (var group in batch.Inputs.GroupBy(input => input.SourceId, StringComparer.Ordinal))
        {
            if (!_assignments.TryGetValue(group.Key, out var assignment))
            {
                _log?.Invoke($"Controller input dropped: sourceId={group.Key} reason=controller is unassigned.");
                continue;
            }

            var accepted = new List<ControllerInputValue>();
            foreach (var input in group.OrderBy(input => input.ControllerSequence))
            {
                if (input.TargetClientIndex is not null
                    && input.TargetClientIndex != assignment.PlayerSlot)
                {
                    LogDroppedInput(input, assignment, "collector supplied a target that does not match the assignment");
                    continue;
                }

                if (_lastControllerSequences.TryGetValue(input.SourceId, out var last)
                    && input.ControllerSequence <= last)
                {
                    LogDroppedInput(input, assignment, $"non-monotonic sequence last={last}");
                    continue;
                }

                _lastControllerSequences[input.SourceId] = input.ControllerSequence;
                accepted.Add(input with { TargetClientIndex = assignment.PlayerSlot });
            }

            if (accepted.Count == 0)
            {
                continue;
            }

            var routed = new ControllerInputBatchMessage(
                _leaseGeneration,
                _assignmentRevision,
                accepted);
            routes.Add(RouteTo(assignment.TargetClientId, CreateEnvelope(
                ControllerControlMessageTypes.InputBatch,
                ControllerControlMessageSerializer.Serialize(routed))));
        }

        return routes;
    }

    private bool IsCurrentCollector(string clientId, long leaseGeneration, out string rejection)
    {
        if (!string.Equals(clientId, _collectorClientId, StringComparison.Ordinal))
        {
            rejection = $"source is not active collector current={_collectorClientId ?? "<none>"}";
            return false;
        }

        if (leaseGeneration != _leaseGeneration)
        {
            rejection = $"lease generation {leaseGeneration} is stale current={_leaseGeneration}";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    private void LogDroppedBatch(string sourceClientId, ControllerInputBatchMessage batch, string reason)
    {
        _log?.Invoke(
            $"Controller input batch dropped: source={sourceClientId} lease={batch.LeaseGeneration} assignmentRevision={batch.AssignmentRevision} count={batch.Inputs.Count} reason={reason}.");
    }

    private void LogDroppedInput(
        ControllerInputValue input,
        BrokerControllerAssignmentState assignment,
        string reason)
    {
        _log?.Invoke(
            $"Controller input dropped: sourceId={input.SourceId} lease={_leaseGeneration} assignmentRevision={_assignmentRevision} sequence={input.ControllerSequence} target={input.TargetClientIndex?.ToString() ?? "<unset>"} expectedTarget={assignment.PlayerSlot} reason={reason}.");
    }

    private BrokerEnvelope CreateLeaseEnvelope(string reason)
    {
        var lease = new CollectorLeaseMessage(_collectorClientId, _leaseGeneration, _assignmentRevision, reason);
        return CreateEnvelope(ControllerControlMessageTypes.CollectorLease, ControllerControlMessageSerializer.Serialize(lease));
    }

    private BrokerEnvelope CreateAssignmentEnvelope()
    {
        var snapshot = new ControllerAssignmentSnapshotMessage(_leaseGeneration, _assignmentRevision, Assignments);
        return CreateEnvelope(ControllerControlMessageTypes.AssignmentSnapshot, ControllerControlMessageSerializer.Serialize(snapshot));
    }

    private BrokerEnvelope CreateEnvelope(string messageType, byte[] payload)
    {
        return new BrokerEnvelope(_sessionId, "broker", null, messageType, payload, ++_outboundSequence);
    }

    private static BrokerRoute RouteTo(string clientId, BrokerEnvelope envelope)
    {
        return new BrokerRoute(clientId, envelope with { TargetClientId = clientId });
    }

    private static IReadOnlyList<BrokerRoute> RoutesToAll(
        IReadOnlyList<BrokerClientRegistration> clients,
        BrokerEnvelope envelope)
    {
        return clients
            .OrderBy(client => client.ClientIndex)
            .Select(client => RouteTo(client.ClientId, envelope))
            .ToArray();
    }

    private static string FormatAssignment(BrokerControllerAssignmentState assignment)
    {
        return $"{assignment.SourceId}:{assignment.PlayerSlot}:{assignment.TargetClientId}:{assignment.ConnectionState}";
    }

    private static string FormatAssignmentIdentity(BrokerControllerAssignmentState assignment)
    {
        return $"{assignment.SourceId}:{assignment.SourceKind}:{assignment.SteamHandle}:{assignment.PlayerSlot}:{assignment.TargetClientId}";
    }

    private sealed record CollectorState(
        CollectorStatusMessage Status,
        DateTimeOffset LastSeen,
        DateTimeOffset? FocusedSince);
}

internal static class QueueExtensions
{
    public static Queue<T> ToQueue<T>(this IEnumerable<T> source)
    {
        return new Queue<T>(source);
    }

    public static bool TryDequeue<T>(this Queue<T> queue, out T value)
    {
        if (queue.Count == 0)
        {
            value = default!;
            return false;
        }

        value = queue.Dequeue();
        return true;
    }
}

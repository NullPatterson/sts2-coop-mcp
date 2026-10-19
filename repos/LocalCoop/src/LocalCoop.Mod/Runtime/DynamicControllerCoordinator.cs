using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HarmonyLib;
using LocalCoop.Mod.Patches;

namespace LocalCoop.Mod.Runtime;

public static class DynamicControllerCoordinator
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<BrokerEnvelope> Inbound = new();
    private static readonly SteamDynamicControllerInput SteamInput = new();
    private static BrokerModeSettings? _settings;
    private static Task<BrokerSharedConnection>? _connectionTask;
    private static BrokerSharedConnection? _connection;
    private static bool _logoReady;
    private static bool _subscribed;
    private static bool? _lastFocused;
    private static DateTimeOffset _nextStatusAt;
    private static DateTimeOffset _nextInventoryAt;
    private static DateTimeOffset _nextConnectAttemptAt;
    private static long _outboundSequence;
    private static long _inventorySequence;
    private static long _leaseGeneration;
    private static long _assignmentRevision;
    private static string? _collectorClientId;
    private static BrokerControllerAssignmentState? _localAssignment;
    private static IReadOnlyList<ControllerSourceDescriptor> _inventory = [];
    private static HashSet<string> _assignedSourceIds = new(StringComparer.Ordinal);
    private static HashSet<string> _assignedSteamSourceIds = new(StringComparer.Ordinal);
    private static string? _lastInventorySummary;
    private static string? _lastAssignmentSummary;
    private static string? _appliedAssignmentSourceId;
    private static string? _activatedAssignmentSourceId;
    private static string? _lastControllerModeActivationFailure;
    private static string? _lastSteamFailure;
    private static int _lastCatalogActionCount = -1;
    private static string? _lastNativeFallbackStatus;
    private static int _lastSteamHandleCount = int.MinValue;
    private static string? _lastPollSummary;
    private static bool? _lastWarmConnectionRefreshSucceeded;

    public static bool IsEnabled => _settings is { Enabled: true, Config: not null };

    public static bool IsActiveCollector => IsEnabled
        && string.Equals(_collectorClientId, _settings!.ClientId, StringComparison.Ordinal);

    public static void Initialize(BrokerModeSettings settings)
    {
        if (!settings.Enabled || settings.Config is null)
        {
            return;
        }

        lock (Gate)
        {
            _settings = settings;
            _connectionTask ??= BrokerSharedConnectionRegistry.GetOrConnectAsync(
                settings.Config,
                settings.ClientId,
                CancellationToken.None);
        }
    }

    public static void MarkLogoReady()
    {
        _logoReady = true;
        Log("Dynamic controller assignment ready: trigger=developer-logo.");
    }

    public static void RememberStrategy(object strategy)
    {
        LocalCoopInputRouter.RememberControllerInputStrategy(strategy);
    }

    public static void Tick(object? controllerManager = null, DateTimeOffset? now = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        _logoReady = true;
        var timestamp = now ?? DateTimeOffset.UtcNow;
        TryAttachConnection(timestamp);
        ProcessInbound();
        if (_connection is null)
        {
            return;
        }

        var strategy = ResolveStrategy(controllerManager);
        var focused = TryIsGameFocusedWindow();
        if (_lastFocused != focused)
        {
            Log($"Dynamic controller focus changed: focused={focused} processId={Environment.ProcessId}.");
        }

        if (strategy is null)
        {
            SendCollectorStatusIfDue(
                focused,
                readiness: null,
                requiredSteamControllerCount: 0,
                requiredNativeControllerCount: 0,
                observedNativeControllerCount: 0,
                timestamp: timestamp,
                force: _lastFocused != focused);
            return;
        }

        ApplyLocalAssignment(strategy);
        ApplyLocalControllerMode(controllerManager);

        try
        {
            var inventoryUpdated = false;
            if (timestamp >= _nextInventoryAt)
            {
                _nextInventoryAt = timestamp + InventoryInterval;
                inventoryUpdated = true;
                var refreshSucceeded = SteamControllerInputSelectionPatches.RefreshDynamicControllerConnections(strategy);
                if (_lastWarmConnectionRefreshSucceeded != refreshSucceeded)
                {
                    _lastWarmConnectionRefreshSucceeded = refreshSucceeded;
                    Log(refreshSucceeded
                        ? "Dynamic Steam warm-standby connection refresh active."
                        : "Dynamic Steam warm-standby connection refresh unavailable.");
                }

                _inventory = SteamInput.ReadInventory(
                    strategy,
                    _settings!.Config!.ControllerClientCount ?? 4,
                    $"{_settings.Config.SessionId}:{_settings.ClientId}:lease{_leaseGeneration}");
                LogSteamCatalogStateIfChanged();
                var summary = string.Join(",", _inventory.Select(FormatControllerSource));
                if (!string.Equals(summary, _lastInventorySummary, StringComparison.Ordinal))
                {
                    _lastInventorySummary = summary;
                    Log($"Dynamic controller inventory: lease={_leaseGeneration} controllers=[{summary}].");
                }

            }

            var actionInventory = _assignedSourceIds.Count == 0
                ? _inventory
                : _inventory.Where(source => _assignedSourceIds.Contains(source.SourceId)).ToArray();
            var requiredSteamControllerCount = _assignedSourceIds.Count == 0
                ? actionInventory.Count(source => source.SourceKind == ControllerSourceKind.SteamInput)
                : _assignedSteamSourceIds.Count;
            var requiredNativeControllerCount = _assignedSourceIds.Count == 0
                ? actionInventory.Count(source => source.SourceKind == ControllerSourceKind.Godot)
                : _assignedSourceIds.Count - _assignedSteamSourceIds.Count;
            var observedNativeControllerCount = actionInventory.Count(source => source.SourceKind == ControllerSourceKind.Godot);
            var inputs = IsActiveCollector
                ? SteamInput.Poll(strategy, actionInventory)
                : [];
            if (!IsActiveCollector)
            {
                SteamInput.Probe(strategy, actionInventory);
            }

            if (!string.Equals(_lastPollSummary, SteamInput.LastPollSummary, StringComparison.Ordinal))
            {
                _lastPollSummary = SteamInput.LastPollSummary;
                Log($"Dynamic Steam poll state: {_lastPollSummary}.");
            }

            SendCollectorStatusIfDue(
                focused,
                SteamInput.LastReadiness,
                requiredSteamControllerCount,
                requiredNativeControllerCount,
                observedNativeControllerCount,
                timestamp,
                force: _lastFocused != focused);

            if (IsActiveCollector && inventoryUpdated)
            {
                Send(ControllerControlMessageTypes.InventorySnapshot, new ControllerInventorySnapshotMessage(
                    _leaseGeneration,
                    ++_inventorySequence,
                    _inventory));
            }

            if (IsActiveCollector && inputs.Count > 0)
            {
                foreach (var input in inputs.Where(input => input.Kind == ControllerInputValueKind.Digital))
                {
                    Log($"Dynamic controller transition: source={input.SourceId} action={input.ActionId} pressed={input.Pressed} sequence={input.ControllerSequence}.");
                }

                Send(ControllerControlMessageTypes.InputBatch, new ControllerInputBatchMessage(
                    _leaseGeneration,
                    _assignmentRevision,
                    inputs));
            }
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            Log($"Dynamic Steam collector unavailable: {FormatException(exception)}.");
            SendCollectorStatusIfDue(
                focused,
                ControllerActionReadiness.Empty,
                requiredSteamControllerCount: 0,
                requiredNativeControllerCount: 0,
                observedNativeControllerCount: 0,
                timestamp: timestamp,
                force: _lastFocused != focused);
        }
    }

    private static void SendCollectorStatusIfDue(
        bool focused,
        ControllerActionReadiness? readiness,
        int requiredSteamControllerCount,
        int requiredNativeControllerCount,
        int observedNativeControllerCount,
        DateTimeOffset timestamp,
        bool force)
    {
        if (!force && timestamp < _nextStatusAt)
        {
            return;
        }

        var steamHandleCount = readiness?.ConnectedControllerCount
            ?? (LocalCoopInputRouter.TryGetRememberedControllerInputStrategy(out var strategy) && strategy is not null
                ? SteamInput.ReadConnectedSteamControllerCount(strategy)
                : -1);
        if (_lastSteamHandleCount != steamHandleCount)
        {
            _lastSteamHandleCount = steamHandleCount;
            Log($"Dynamic Steam handles observed: count={steamHandleCount} focused={focused} processId={Environment.ProcessId}.");
        }

        _lastFocused = focused;
        _nextStatusAt = timestamp + StatusInterval;
        Send(ControllerControlMessageTypes.CollectorStatus, new CollectorStatusMessage(
            _settings!.Config!.ClientIndex,
            focused,
            readiness is not null,
            _logoReady,
            _settings.Config.InputMode != BrokerClientInputMode.None,
            _settings.Config.ControllerClientCount ?? 4,
            ActionDataReady: IsActionDataReady(
                readiness,
                requiredSteamControllerCount,
                requiredNativeControllerCount,
                observedNativeControllerCount,
                focused),
            ConnectedControllerCount: Math.Max(0, steamHandleCount),
            ActiveDigitalActionCount: readiness?.ActiveDigitalActionCount ?? 0,
            DigitalActionQueryCount: readiness?.DigitalActionQueryCount ?? 0,
            ActiveAnalogActionCount: readiness?.ActiveAnalogActionCount ?? 0,
            AnalogActionQueryCount: readiness?.AnalogActionQueryCount ?? 0));
    }

    private static bool IsActionDataReady(
        ControllerActionReadiness? readiness,
        int requiredSteamControllerCount,
        int requiredNativeControllerCount,
        int observedNativeControllerCount,
        bool focused)
    {
        var steamReady = requiredSteamControllerCount == 0
            || (readiness?.IsReady == true
                && readiness.ConnectedControllerCount >= requiredSteamControllerCount);
        var nativeReady = requiredNativeControllerCount == 0
            || (focused && observedNativeControllerCount >= requiredNativeControllerCount);
        return requiredSteamControllerCount + requiredNativeControllerCount > 0
            && steamReady
            && nativeReady;
    }

    public static bool IsActionDataReadyForTesting(
        ControllerActionReadiness? readiness,
        int requiredSteamControllerCount,
        int requiredNativeControllerCount = 0,
        int observedNativeControllerCount = 0,
        bool focused = true)
    {
        return IsActionDataReady(
            readiness,
            requiredSteamControllerCount,
            requiredNativeControllerCount,
            observedNativeControllerCount,
            focused);
    }

    public static bool ShouldAllowControllerInput(object? inputEvent)
    {
        if (!IsEnabled || inputEvent is null)
        {
            return true;
        }

        if (!IsRawJoypadInputForTesting(inputEvent))
        {
            return true;
        }

        return SteamControllerInputSelection.IsGeneratedInputEvent(inputEvent);
    }

    public static bool IsRawJoypadInputForTesting(object inputEvent)
    {
        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        return typeName.Contains("Joypad", StringComparison.OrdinalIgnoreCase);
    }

    public static void ObservePhysicalInput(object? inputEvent)
    {
        if (!IsActiveCollector || inputEvent is null
            || !LocalCoopInputRouter.TryGetRememberedControllerInputStrategy(out var strategy)
            || strategy is null)
        {
            return;
        }

        try
        {
            var inputs = SteamInput.TranslateNativeEvent(strategy, inputEvent, _inventory);
            if (inputs.Count > 0)
            {
                foreach (var input in inputs.Where(input => input.Kind == ControllerInputValueKind.Digital))
                {
                    Log($"Dynamic native controller transition: source={input.SourceId} action={input.ActionId} pressed={input.Pressed} sequence={input.ControllerSequence}.");
                }

                Send(ControllerControlMessageTypes.InputBatch, new ControllerInputBatchMessage(
                    _leaseGeneration,
                    _assignmentRevision,
                    inputs));
            }
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            Log($"Dynamic native controller translation failed: {FormatException(exception)}.");
        }
    }

    public static void ResetForTesting()
    {
        lock (Gate)
        {
            if (_connection is not null && _subscribed)
            {
                _connection.ControllerEnvelopeReceived -= QueueControllerEnvelope;
            }

            _settings = null;
            _connectionTask = null;
            _connection = null;
            _logoReady = false;
            _subscribed = false;
            _lastFocused = null;
            _nextStatusAt = default;
            _nextInventoryAt = default;
            _nextConnectAttemptAt = default;
            _outboundSequence = 0;
            _inventorySequence = 0;
            _leaseGeneration = 0;
            _assignmentRevision = 0;
            _collectorClientId = null;
            _localAssignment = null;
            _inventory = [];
            _assignedSourceIds = new HashSet<string>(StringComparer.Ordinal);
            _assignedSteamSourceIds = new HashSet<string>(StringComparer.Ordinal);
            _lastInventorySummary = null;
            _lastAssignmentSummary = null;
            _appliedAssignmentSourceId = null;
            _activatedAssignmentSourceId = null;
            _lastControllerModeActivationFailure = null;
            _lastSteamFailure = null;
            _lastCatalogActionCount = -1;
            _lastNativeFallbackStatus = null;
            _lastSteamHandleCount = int.MinValue;
            _lastPollSummary = null;
            _lastWarmConnectionRefreshSucceeded = null;
            while (Inbound.TryDequeue(out _))
            {
            }
        }
    }

    private static void TryAttachConnection(DateTimeOffset now)
    {
        if (_connection?.IsClosed == true)
        {
            if (_subscribed)
            {
                _connection.ControllerEnvelopeReceived -= QueueControllerEnvelope;
            }

            _connection = null;
            _connectionTask = null;
            _subscribed = false;
            _nextConnectAttemptAt = now + TimeSpan.FromSeconds(1);
            Log("Dynamic controller broker connection closed; reconnect scheduled.");
        }

        if (_connection is not null)
        {
            return;
        }

        if (_connectionTask is null)
        {
            if (now < _nextConnectAttemptAt || _settings?.Config is null)
            {
                return;
            }

            _connectionTask = BrokerSharedConnectionRegistry.GetOrConnectAsync(
                _settings.Config,
                _settings.ClientId,
                CancellationToken.None);
        }

        if (!_connectionTask.IsCompleted)
        {
            return;
        }

        if (!_connectionTask.IsCompletedSuccessfully)
        {
            Log($"Dynamic controller broker connection failed: {_connectionTask.Exception?.GetBaseException().Message ?? "unknown error"}.");
            _connectionTask = null;
            _nextConnectAttemptAt = now + TimeSpan.FromSeconds(1);
            return;
        }

        _connection = _connectionTask.Result;
        if (!_subscribed)
        {
            _connection.ControllerEnvelopeReceived += QueueControllerEnvelope;
            _subscribed = true;
        }

        Log("Dynamic controller broker connection active.");
    }

    private static void QueueControllerEnvelope(BrokerEnvelope envelope)
    {
        Inbound.Enqueue(envelope);
    }

    private static void ProcessInbound()
    {
        while (Inbound.TryDequeue(out var envelope))
        {
            try
            {
                switch (envelope.MessageType)
                {
                    case ControllerControlMessageTypes.CollectorLease:
                    {
                        var lease = ControllerControlMessageSerializer.Deserialize<CollectorLeaseMessage>(envelope);
                        if (lease.LeaseGeneration != _leaseGeneration)
                        {
                            ReleaseLocalInputs();
                            SteamInput.ResetPollingState();
                        }

                        _collectorClientId = lease.CollectorClientId;
                        _leaseGeneration = lease.LeaseGeneration;
                        _assignmentRevision = lease.AssignmentRevision;
                        Log($"Dynamic controller lease: collector={lease.CollectorClientId ?? "<none>"} generation={lease.LeaseGeneration} assignmentRevision={lease.AssignmentRevision} reason={lease.Reason}.");
                        break;
                    }
                    case ControllerControlMessageTypes.AssignmentSnapshot:
                    {
                        var snapshot = ControllerControlMessageSerializer.Deserialize<ControllerAssignmentSnapshotMessage>(envelope);
                        var previous = _localAssignment;
                        _leaseGeneration = snapshot.LeaseGeneration;
                        _assignmentRevision = snapshot.AssignmentRevision;
                        _localAssignment = snapshot.Assignments.FirstOrDefault(assignment =>
                            string.Equals(assignment.TargetClientId, _settings!.ClientId, StringComparison.Ordinal));
                        _assignedSourceIds = snapshot.Assignments
                            .Select(assignment => assignment.SourceId)
                            .ToHashSet(StringComparer.Ordinal);
                        _assignedSteamSourceIds = snapshot.Assignments
                            .Where(assignment => assignment.SourceKind == ControllerSourceKind.SteamInput)
                            .Select(assignment => assignment.SourceId)
                            .ToHashSet(StringComparer.Ordinal);
                        if (!HasSameAssignmentIdentity(previous, _localAssignment))
                        {
                            ReleaseLocalInputs();
                            _appliedAssignmentSourceId = null;
                            _activatedAssignmentSourceId = null;
                            _lastControllerModeActivationFailure = null;
                        }

                        var summary = string.Join(",", snapshot.Assignments.Select(assignment =>
                            $"{assignment.SourceId}->slot{assignment.PlayerSlot}:{assignment.ConnectionState}"));
                        if (!string.Equals(summary, _lastAssignmentSummary, StringComparison.Ordinal))
                        {
                            _lastAssignmentSummary = summary;
                            Log($"Dynamic controller assignments: revision={snapshot.AssignmentRevision} assignments=[{summary}] local={_localAssignment?.SourceId ?? "<none>"}.");
                        }

                        break;
                    }
                    case ControllerControlMessageTypes.InputBatch:
                    {
                        var batch = ControllerControlMessageSerializer.Deserialize<ControllerInputBatchMessage>(envelope);
                        if (batch.LeaseGeneration != _leaseGeneration || batch.AssignmentRevision != _assignmentRevision)
                        {
                            Log($"Dynamic controller input dropped locally: lease={batch.LeaseGeneration}/{_leaseGeneration} assignmentRevision={batch.AssignmentRevision}/{_assignmentRevision}.");
                            break;
                        }

                        if (!LocalCoopInputRouter.TryGetRememberedControllerInputStrategy(out var strategy) || strategy is null)
                        {
                            Log("Dynamic controller input dropped locally: Steam input strategy unavailable.");
                            break;
                        }

                        foreach (var input in batch.Inputs)
                        {
                            if (input.TargetClientIndex != _settings!.Config!.ClientIndex
                                || _localAssignment is null
                                || !string.Equals(input.SourceId, _localAssignment.SourceId, StringComparison.Ordinal))
                            {
                                Log(
                                    $"Dynamic controller input dropped locally: source={input.SourceId} lease={batch.LeaseGeneration} assignmentRevision={batch.AssignmentRevision} sequence={input.ControllerSequence} target={input.TargetClientIndex?.ToString() ?? "<unset>"} expectedTarget={_settings.Config.ClientIndex} assignedSource={_localAssignment?.SourceId ?? "<none>"} reason=target or assignment mismatch.");
                                continue;
                            }

                            if (!SteamInput.Inject(strategy, input))
                            {
                                Log($"Dynamic controller input injection failed: source={input.SourceId} lease={batch.LeaseGeneration} assignmentRevision={batch.AssignmentRevision} sequence={input.ControllerSequence} target={input.TargetClientIndex} action={input.ActionId} kind={input.Kind}.");
                            }
                            else if (input.Kind == ControllerInputValueKind.Digital)
                            {
                                Log($"Dynamic controller input injected: source={input.SourceId} sequence={input.ControllerSequence} target={input.TargetClientIndex} action={input.ActionId} pressed={input.Pressed}.");
                            }
                        }

                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
            {
                Log($"Dynamic controller envelope failed: type={envelope.MessageType} error={FormatException(exception)}.");
            }
        }
    }

    private static object? ResolveStrategy(object? controllerManager)
    {
        var strategy = controllerManager?.GetType()
            .GetField("_inputStrategy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(controllerManager);
        if (strategy is not null)
        {
            RememberStrategy(strategy);
            return strategy;
        }

        return LocalCoopInputRouter.TryGetRememberedControllerInputStrategy(out strategy) ? strategy : null;
    }

    private static void ApplyLocalAssignment(object strategy)
    {
        if (_localAssignment is not { SourceKind: ControllerSourceKind.SteamInput, SteamHandle: not null })
        {
            SteamInput.ClearAssignedHandle(strategy);
            _appliedAssignmentSourceId = null;
            return;
        }

        if (string.Equals(_appliedAssignmentSourceId, _localAssignment.SourceId, StringComparison.Ordinal))
        {
            return;
        }

        if (SteamInput.ApplyAssignedHandle(strategy, _localAssignment.SteamHandle))
        {
            _appliedAssignmentSourceId = _localAssignment.SourceId;
            Log($"Dynamic controller strategy assigned: source={_localAssignment.SourceId} handle={_localAssignment.SteamHandle}.");
        }
    }

    private static bool HasSameAssignmentIdentity(
        BrokerControllerAssignmentState? previous,
        BrokerControllerAssignmentState? current)
    {
        return previous is null && current is null
            || previous is not null
            && current is not null
            && string.Equals(previous.SourceId, current.SourceId, StringComparison.Ordinal)
            && previous.SourceKind == current.SourceKind
            && string.Equals(previous.SteamHandle, current.SteamHandle, StringComparison.Ordinal)
            && previous.PlayerSlot == current.PlayerSlot
            && string.Equals(previous.TargetClientId, current.TargetClientId, StringComparison.Ordinal);
    }

    private static void ApplyLocalControllerMode(object? controllerManager)
    {
        if (!_logoReady || controllerManager is null || _localAssignment is null)
        {
            return;
        }

        if (string.Equals(_activatedAssignmentSourceId, _localAssignment.SourceId, StringComparison.Ordinal))
        {
            return;
        }

        if (DynamicControllerModeActivation.TryActivate(controllerManager, out var reason))
        {
            _activatedAssignmentSourceId = _localAssignment.SourceId;
            _lastControllerModeActivationFailure = null;
            Log($"Dynamic controller mode activated at logo assignment: source={_localAssignment.SourceId} playerSlot={_localAssignment.PlayerSlot} reason=\"{reason}\".");
            return;
        }

        if (!string.Equals(_lastControllerModeActivationFailure, reason, StringComparison.Ordinal))
        {
            _lastControllerModeActivationFailure = reason;
            Log($"Dynamic controller mode activation pending: source={_localAssignment.SourceId} reason=\"{reason}\".");
        }
    }

    private static void ReleaseLocalInputs()
    {
        if (LocalCoopInputRouter.TryGetRememberedControllerInputStrategy(out var strategy) && strategy is not null)
        {
            SteamInput.ReleaseDeliveredInputs(strategy);
        }
    }

    private static void Send<T>(string messageType, T message)
    {
        if (_connection is null || _settings?.Config is null)
        {
            return;
        }

        var envelope = new BrokerEnvelope(
            _settings.Config.SessionId,
            _settings.ClientId,
            null,
            messageType,
            ControllerControlMessageSerializer.Serialize(message),
            Interlocked.Increment(ref _outboundSequence));
        _ = _connection.SendEnvelopeAsync(envelope, CancellationToken.None).ContinueWith(
            task => Log($"Dynamic controller send failed: type={messageType} error={task.Exception?.GetBaseException().Message}."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static bool TryIsGameFocusedWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
                return foregroundProcessId == (uint)Environment.ProcessId;
            }
        }

        try
        {
            var gameType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
            return gameType?.GetMethod("IsGameFocusedWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(null, null) is true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private static string FormatControllerSource(ControllerSourceDescriptor controller)
    {
        return $"{controller.SourceId}" +
            $"{{kind={controller.SourceKind}" +
            $",state={controller.ConnectionState}" +
            $",type={controller.ControllerType ?? "<none>"}" +
            $",device={controller.GodotDeviceId?.ToString() ?? "<none>"}" +
            $",steamIndex={controller.SteamInputIndex?.ToString() ?? "<none>"}" +
            $",xinputIndex={controller.XInputIndex?.ToString() ?? "<none>"}" +
            $",vid={controller.VendorId?.ToString() ?? "<none>"}" +
            $",pid={controller.ProductId?.ToString() ?? "<none>"}" +
            $",serial={controller.SerialNumber ?? "<none>"}" +
            $",name={controller.RawName ?? "<none>"}}}";
    }

    private static string FormatException(Exception exception)
    {
        var leaf = exception;
        while (leaf is TargetInvocationException { InnerException: not null } invocation)
        {
            leaf = invocation.InnerException!;
        }

        return $"{leaf.GetType().Name}: {leaf.Message}";
    }

    private static void LogSteamCatalogStateIfChanged()
    {
        if (!string.Equals(_lastSteamFailure, SteamInput.LastSteamFailure, StringComparison.Ordinal))
        {
            if (SteamInput.LastSteamFailure is null)
            {
                Log("Dynamic Steam catalog recovered; Steam Input is primary again.");
            }
            else
            {
                Log(
                    $"Dynamic Steam catalog failed closed: error={SteamInput.LastSteamFailure} gameRelease={ReadGameReleaseVersion()} fallback=native-only.");
            }

            _lastSteamFailure = SteamInput.LastSteamFailure;
        }

        if (_lastCatalogActionCount != SteamInput.CatalogActionCount)
        {
            _lastCatalogActionCount = SteamInput.CatalogActionCount;
            Log($"Dynamic Steam action catalog resolved: digitalActions={SteamInput.CatalogActionCount} analogAction=joystick.");
        }

        if (!string.Equals(_lastNativeFallbackStatus, SteamInput.NativeFallbackStatus, StringComparison.Ordinal))
        {
            _lastNativeFallbackStatus = SteamInput.NativeFallbackStatus;
            Log($"Dynamic native fallback state: {SteamInput.NativeFallbackStatus}.");
        }
    }

    private static string ReadGameReleaseVersion()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "release_info.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? "<unknown>"
                : "<missing-version>";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"<unavailable:{exception.GetType().Name}>";
        }
    }

    private static void Log(string message)
    {
        if (_settings is not null)
        {
            new BrokerEventLog(_settings.EventLogPath).Write(message);
        }
    }
}

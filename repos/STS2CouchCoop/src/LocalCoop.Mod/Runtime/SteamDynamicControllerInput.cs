using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace LocalCoop.Mod.Runtime;

public sealed record ControllerActionReadiness(
    int ConnectedControllerCount,
    int ActiveDigitalActionCount,
    int DigitalActionQueryCount,
    int ActiveAnalogActionCount,
    int AnalogActionQueryCount)
{
    public bool IsReady => ConnectedControllerCount > 0
        && DigitalActionQueryCount > 0
        && ActiveDigitalActionCount == DigitalActionQueryCount
        && (AnalogActionQueryCount == 0 || ActiveAnalogActionCount == AnalogActionQueryCount);

    public static ControllerActionReadiness Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed class SteamDynamicControllerInput
{
    private sealed record DigitalActionCatalogEntry(object ActionHandle, object Template, string ActionId);

    private const string AnalogActionId = "__steam_joystick__";
    private static readonly TimeSpan DefaultNativeFallbackDelay = TimeSpan.FromSeconds(30);
    private static readonly BindingFlags Members =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly TimeSpan _nativeFallbackDelay;
    private readonly Dictionary<string, bool> _digitalStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float X, float Y)> _analogStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControllerSourceDescriptor> _committedNativeSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveredPressedActions = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _readySteamHandles = [];
    private DateTimeOffset? _nativeFallbackObservedAt;
    private bool _catalogInitialized;

    public SteamDynamicControllerInput(TimeSpan? nativeFallbackDelay = null)
    {
        _nativeFallbackDelay = nativeFallbackDelay ?? DefaultNativeFallbackDelay;
    }

    public string? LastSteamFailure { get; private set; }

    public int CatalogActionCount { get; private set; }

    public string LastPollSummary { get; private set; } = "not-polled";

    public ControllerActionReadiness LastReadiness { get; private set; } = ControllerActionReadiness.Empty;

    public string NativeFallbackStatus { get; private set; } = "not-observed";

    public int ReadConnectedSteamControllerCount(object strategy)
    {
        try
        {
            RunSteamInputFrame();
            return GetConnectedHandles(strategy).Count;
        }
        catch (Exception exception) when (exception is TargetInvocationException
            or MissingMemberException
            or InvalidOperationException
            or ArgumentException)
        {
            return -1;
        }
    }

    public IReadOnlyList<ControllerSourceDescriptor> ReadInventory(
        object strategy,
        int expectedControllerCount,
        string nativeSourceScope = "local",
        DateTimeOffset? now = null)
    {
        IReadOnlyList<object> handles = [];
        var result = new List<ControllerSourceDescriptor>();
        var steamProbeSucceeded = false;
        try
        {
            RunSteamInputFrame();
            handles = GetConnectedHandles(strategy);
            steamProbeSucceeded = true;
            _readySteamHandles.IntersectWith(handles.Select(ReadNumericHandle));
            var actionSet = GetActionSetHandle(strategy);
            foreach (var handle in handles)
            {
                if (actionSet is not null)
                {
                    InvokeSteam("ActivateActionSet", [handle, actionSet]);
                }

                var numericHandle = ReadNumericHandle(handle).ToString();
                var controllerType = InvokeSteam("GetInputTypeForHandle", [handle])?.ToString();
                if (!_catalogInitialized)
                {
                    InitializeCatalog(strategy, handle);
                }
                var numericHandleValue = ReadNumericHandle(handle);
                var connectionState = EvaluateConnectionState(strategy, handle, actionSet);
                if (connectionState == ControllerConnectionState.Ready)
                {
                    _readySteamHandles.Add(numericHandleValue);
                }
                else if (_readySteamHandles.Contains(numericHandleValue))
                {
                    connectionState = ControllerConnectionState.Ready;
                }
                result.Add(new ControllerSourceDescriptor(
                    $"steam:{numericHandle}",
                    ControllerSourceKind.SteamInput,
                    numericHandle,
                    controllerType,
                    ConnectionState: connectionState));
            }

            LastSteamFailure = null;
        }
        catch (Exception exception) when (exception is TargetInvocationException
            or MissingMemberException
            or InvalidOperationException
            or ArgumentException)
        {
            LastSteamFailure = FormatSteamFailure(exception);
            handles = [];
            result.Clear();
        }

        AddNativeFallbackControllers(
            result,
            handles,
            expectedControllerCount,
            nativeSourceScope,
            steamProbeSucceeded,
            now ?? DateTimeOffset.UtcNow);

        return result;
    }

    public IReadOnlyList<ControllerInputValue> TranslateNativeEvent(
        object strategy,
        object inputEvent,
        IReadOnlyList<ControllerSourceDescriptor> inventory)
    {
        var device = GetProperty(inputEvent, "Device") is int value ? value : -1;
        var source = inventory.FirstOrDefault(controller =>
            controller.SourceKind == ControllerSourceKind.Godot
            && controller.GodotDeviceId == device);
        if (source is null)
        {
            return [];
        }

        var translated = new List<ControllerInputValue>();
        foreach (var actionId in GetMatchingGodotActions(inputEvent))
        {
            var pressed = GetProperty(inputEvent, "Pressed") is bool state
                ? state
                : ReadInputActionStrength(inputEvent, actionId) > 0.5f;
            ReleaseGodotAction(actionId);
            var stateKey = $"{source.SourceId}|{actionId}";
            if (!_digitalStates.TryGetValue(stateKey, out var previous))
            {
                _digitalStates[stateKey] = pressed;
                if (!pressed)
                {
                    continue;
                }
            }
            else if (previous == pressed)
            {
                continue;
            }

            _digitalStates[stateKey] = pressed;
            translated.Add(new ControllerInputValue(
                source.SourceId,
                NextSequence(source.SourceId),
                actionId,
                ControllerInputValueKind.Digital,
                pressed));
        }

        var inputAxis = GetProperty(inputEvent, "Axis");
        var axisValue = GetProperty(inputEvent, "AxisValue") is IConvertible axis
            ? axis.ToSingle(null)
            : (float?)null;
        if (inputAxis is not null && axisValue is not null)
        {
            var xTemplate = strategy.GetType().GetField("_joystickXAxis", Members)?.GetValue(strategy);
            var yTemplate = strategy.GetType().GetField("_joystickYAxis", Members)?.GetValue(strategy);
            var previous = _analogStates.GetValueOrDefault(source.SourceId);
            var x = Equals(inputAxis, GetProperty(xTemplate, "Axis")) ? axisValue.Value : previous.X;
            var y = Equals(inputAxis, GetProperty(yTemplate, "Axis")) ? axisValue.Value : previous.Y;
            if (Math.Abs(previous.X - x) >= 0.01f || Math.Abs(previous.Y - y) >= 0.01f)
            {
                _analogStates[source.SourceId] = (x, y);
                translated.Add(new ControllerInputValue(
                    source.SourceId,
                    NextSequence(source.SourceId),
                    AnalogActionId,
                    ControllerInputValueKind.Analog,
                    X: x,
                    Y: y));
            }
        }

        return translated;
    }

    public IReadOnlyList<ControllerInputValue> Poll(object strategy, IReadOnlyList<ControllerSourceDescriptor> inventory)
    {
        RunSteamInputFrame();
        var handles = GetConnectedHandles(strategy)
            .ToDictionary(handle => $"steam:{ReadNumericHandle(handle)}", StringComparer.Ordinal);
        var actionSet = GetActionSetHandle(strategy);
        var inputs = new List<ControllerInputValue>();
        var digitalQueries = 0;
        var activeDigitalActions = 0;
        var pressedActions = new List<string>();
        var analogQueries = 0;
        var activeAnalogActions = 0;
        var promotedBindingControllers = 0;
        var queriedControllers = 0;

        foreach (var source in inventory)
        {
            if (!handles.TryGetValue(source.SourceId, out var handle))
            {
                continue;
            }

            queriedControllers++;

            if (actionSet is not null)
            {
                InvokeSteam("ActivateActionSet", [handle, actionSet]);
            }

            var digitalActiveBefore = activeDigitalActions;
            var analogActiveBefore = activeAnalogActions;
            PollDigitalActions(
                strategy,
                source.SourceId,
                handle,
                inputs,
                ref digitalQueries,
                ref activeDigitalActions,
                pressedActions);
            PollAnalogAction(
                strategy,
                source.SourceId,
                handle,
                inputs,
                ref analogQueries,
                ref activeAnalogActions);
            if (source.ConnectionState == ControllerConnectionState.Binding
                && (activeDigitalActions > digitalActiveBefore || activeAnalogActions > analogActiveBefore))
            {
                _readySteamHandles.Add(ReadNumericHandle(handle));
                promotedBindingControllers++;
            }
        }

        LastReadiness = new ControllerActionReadiness(
            queriedControllers,
            activeDigitalActions,
            digitalQueries,
            activeAnalogActions,
            analogQueries);
        LastPollSummary = $"handles={handles.Count} ready={inventory.Count(source => source.ConnectionState == ControllerConnectionState.Ready) + promotedBindingControllers} "
            + $"promoted={promotedBindingControllers} "
            + $"digitalActive={activeDigitalActions}/{digitalQueries} pressed=[{string.Join(",", pressedActions)}] "
            + $"analogActive={activeAnalogActions}/{analogQueries}";
        return inputs;
    }

    public ControllerActionReadiness Probe(object strategy, IReadOnlyList<ControllerSourceDescriptor> inventory)
    {
        RunSteamInputFrame();
        var handles = GetConnectedHandles(strategy)
            .ToDictionary(handle => $"steam:{ReadNumericHandle(handle)}", StringComparer.Ordinal);
        var actionSet = GetActionSetHandle(strategy);
        var digitalQueries = 0;
        var activeDigitalActions = 0;
        var analogQueries = 0;
        var activeAnalogActions = 0;
        var queriedControllers = 0;

        foreach (var source in inventory.Where(source => source.SourceKind == ControllerSourceKind.SteamInput))
        {
            if (!handles.TryGetValue(source.SourceId, out var handle))
            {
                continue;
            }

            queriedControllers++;

            if (actionSet is not null)
            {
                InvokeSteam("ActivateActionSet", [handle, actionSet]);
            }

            foreach (var entry in ReadDigitalActionCatalog(strategy))
            {
                var data = InvokeSteam("GetDigitalActionData", [handle, entry.ActionHandle]);
                digitalQueries++;
                if (ReadBool(data, "bActive", "Active"))
                {
                    activeDigitalActions++;
                }
            }

            var analogHandle = strategy.GetType().GetField("_joystickActionHandle", Members)?.GetValue(strategy);
            if (analogHandle is not null)
            {
                var data = InvokeSteam("GetAnalogActionData", [handle, analogHandle]);
                analogQueries++;
                if (ReadBool(data, "bActive", "Active"))
                {
                    activeAnalogActions++;
                }
            }
        }

        LastReadiness = new ControllerActionReadiness(
            queriedControllers,
            activeDigitalActions,
            digitalQueries,
            activeAnalogActions,
            analogQueries);
        LastPollSummary = $"handles={handles.Count} standby=true digitalActive={activeDigitalActions}/{digitalQueries} analogActive={activeAnalogActions}/{analogQueries}";
        return LastReadiness;
    }

    public void ResetPollingState()
    {
        _digitalStates.Clear();
        _analogStates.Clear();
        _sequences.Clear();
        LastReadiness = ControllerActionReadiness.Empty;
        LastPollSummary = "lease-reset";
    }

    public bool ApplyAssignedHandle(object strategy, string steamHandle)
    {
        var field = strategy.GetType().GetField("_currentControllerHandle", Members);
        if (field is null || !ulong.TryParse(steamHandle, out var numericHandle))
        {
            return false;
        }

        var handleType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        var handle = GetConnectedHandles(strategy)
            .FirstOrDefault(candidate => ReadNumericHandle(candidate) == numericHandle)
            ?? CreateHandle(handleType, numericHandle);
        if (handle is null)
        {
            return false;
        }

        field.SetValue(strategy, handle);
        var actionSet = GetActionSetHandle(strategy);
        if (actionSet is not null)
        {
            InvokeSteam("ActivateActionSet", [handle, actionSet]);
        }

        var controllerType = InvokeSteam("GetInputTypeForHandle", [handle]);
        if (controllerType is not null)
        {
            strategy.GetType().GetMethod("UpdateControllerConfig", Members, [controllerType.GetType()])
                ?.Invoke(strategy, [controllerType]);
            strategy.GetType().GetMethod("UpdateInputMap", Members, Type.EmptyTypes)
                ?.Invoke(strategy, null);
        }

        return true;
    }

    public void ClearAssignedHandle(object strategy)
    {
        strategy.GetType().GetField("_currentControllerHandle", Members)?.SetValue(strategy, null);
    }

    public bool Inject(object strategy, ControllerInputValue input)
    {
        if (input.Kind == ControllerInputValueKind.Analog)
        {
            return InjectAnalog(strategy, input.X, input.Y);
        }

        var template = FindDigitalTemplate(strategy, input.ActionId);
        var generated = template is null
            ? CreateDigitalInputEvent(input.ActionId)
            : Duplicate(template);
        if (generated is null)
        {
            return false;
        }

        SetProperty(generated, "Pressed", input.Pressed);
        SetProperty(generated, "Strength", input.Pressed ? 1f : 0f);
        SteamControllerInputSelection.RegisterGeneratedInputEvents([generated]);
        ParseInputEvent(generated);
        if (input.Pressed)
        {
            _deliveredPressedActions.Add(input.ActionId);
        }
        else
        {
            _deliveredPressedActions.Remove(input.ActionId);
        }

        return true;
    }

    public void ReleaseDeliveredInputs(object strategy)
    {
        foreach (var action in _deliveredPressedActions.ToArray())
        {
            Inject(strategy, new ControllerInputValue(
                "release",
                0,
                action,
                ControllerInputValueKind.Digital,
                Pressed: false));
        }

        _deliveredPressedActions.Clear();
        InjectAnalog(strategy, 0, 0);
    }

    private void PollDigitalActions(
        object strategy,
        string sourceId,
        object handle,
        List<ControllerInputValue> inputs,
        ref int digitalQueries,
        ref int activeDigitalActions,
        List<string> pressedActions)
    {
        foreach (var entry in ReadDigitalActionCatalog(strategy))
        {
            var data = InvokeSteam("GetDigitalActionData", [handle, entry.ActionHandle]);
            var active = ReadBool(data, "bActive", "Active");
            var pressed = active && ReadBool(data, "bState", "State");
            digitalQueries++;
            if (active)
            {
                activeDigitalActions++;
            }

            if (pressed)
            {
                pressedActions.Add($"{sourceId}:{entry.ActionId}");
            }

            var stateKey = $"{sourceId}|{entry.ActionId}";
            if (!_digitalStates.TryGetValue(stateKey, out var previous))
            {
                _digitalStates[stateKey] = pressed;
                if (!pressed)
                {
                    continue;
                }
            }
            else if (previous == pressed)
            {
                continue;
            }

            _digitalStates[stateKey] = pressed;
            inputs.Add(new ControllerInputValue(
                sourceId,
                NextSequence(sourceId),
                entry.ActionId,
                ControllerInputValueKind.Digital,
                pressed));
        }
    }

    private void PollAnalogAction(
        object strategy,
        string sourceId,
        object handle,
        List<ControllerInputValue> inputs,
        ref int analogQueries,
        ref int activeAnalogActions)
    {
        var actionHandle = strategy.GetType().GetField("_joystickActionHandle", Members)?.GetValue(strategy);
        if (actionHandle is null)
        {
            return;
        }

        var data = InvokeSteam("GetAnalogActionData", [handle, actionHandle]);
        var active = ReadBool(data, "bActive", "Active");
        analogQueries++;
        if (active)
        {
            activeAnalogActions++;
        }

        var x = active ? ReadFloat(data, "x", "X") : 0;
        var y = active ? ReadFloat(data, "y", "Y") : 0;
        if (_analogStates.TryGetValue(sourceId, out var previous)
            && Math.Abs(previous.X - x) < 0.01f
            && Math.Abs(previous.Y - y) < 0.01f)
        {
            return;
        }

        _analogStates[sourceId] = (x, y);
        inputs.Add(new ControllerInputValue(
            sourceId,
            NextSequence(sourceId),
            AnalogActionId,
            ControllerInputValueKind.Analog,
            X: x,
            Y: y));
    }

    private static bool InjectAnalog(object strategy, float x, float y)
    {
        var xTemplate = strategy.GetType().GetField("_joystickXAxis", Members)?.GetValue(strategy);
        var yTemplate = strategy.GetType().GetField("_joystickYAxis", Members)?.GetValue(strategy);
        if (xTemplate is null || yTemplate is null || Duplicate(xTemplate) is not { } xEvent || Duplicate(yTemplate) is not { } yEvent)
        {
            return false;
        }

        SetProperty(xEvent, "AxisValue", x);
        SetProperty(yEvent, "AxisValue", y);
        SteamControllerInputSelection.RegisterGeneratedInputEvents([xEvent, yEvent]);
        ParseInputEvent(xEvent);
        ParseInputEvent(yEvent);
        return true;
    }

    private long NextSequence(string sourceId)
    {
        var next = _sequences.GetValueOrDefault(sourceId) + 1;
        _sequences[sourceId] = next;
        return next;
    }

    private static object? FindDigitalTemplate(object strategy, string actionId)
    {
        if (strategy.GetType().GetField("_inputEvents", Members)?.GetValue(strategy) is not IDictionary inputEvents)
        {
            return null;
        }

        foreach (DictionaryEntry entry in inputEvents)
        {
            if (string.Equals(GetProperty(entry.Value, "Action")?.ToString(), actionId, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static IReadOnlyList<object> GetConnectedHandles(object strategy)
    {
        var field = strategy.GetType().GetField("_currentControllerHandle", Members)
            ?? throw new MissingMemberException(strategy.GetType().FullName, "_currentControllerHandle");
        var handleType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        var handles = Array.CreateInstance(handleType, ReadSteamConstant("STEAM_INPUT_MAX_COUNT"));
        var count = InvokeSteam("GetConnectedControllers", [handles]) is int value ? value : 0;
        return Enumerable.Range(0, Math.Clamp(count, 0, handles.Length))
            .Select(index => handles.GetValue(index))
            .Where(handle => handle is not null)
            .Cast<object>()
            .ToArray();
    }

    private void InitializeCatalog(object strategy, object handle)
    {
        var controllerType = InvokeSteam("GetInputTypeForHandle", [handle]);
        if (controllerType is not null)
        {
            strategy.GetType().GetMethod("UpdateControllerConfig", Members, [controllerType.GetType()])
                ?.Invoke(strategy, [controllerType]);
            strategy.GetType().GetMethod("UpdateInputMap", Members, Type.EmptyTypes)
                ?.Invoke(strategy, null);
        }

        strategy.GetType().GetMethod("GetDefaultControllerInputMap", Members, Type.EmptyTypes)
            ?.Invoke(strategy, null);
        CatalogActionCount = ReadDigitalActionCatalog(strategy).Count;
        _catalogInitialized = CatalogActionCount > 0;
    }

    private static ControllerConnectionState EvaluateConnectionState(
        object strategy,
        object handle,
        object? actionSet)
    {
        if (actionSet is null)
        {
            return ControllerConnectionState.Binding;
        }

        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        var getBindingRevision = steamInputType.GetMethods(Members)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "GetDeviceBindingRevision", StringComparison.Ordinal)
                && method.GetParameters() is
                [var handleParameter, var majorParameter, var minorParameter]
                && handleParameter.ParameterType.IsAssignableFrom(handle.GetType())
                && majorParameter.ParameterType.IsByRef
                && minorParameter.ParameterType.IsByRef);
        if (getBindingRevision is not null)
        {
            object?[] arguments = [handle, 0, 0];
            if (getBindingRevision.Invoke(null, arguments) is true)
            {
                return ControllerConnectionState.Ready;
            }
        }

        return HasActiveActionOrigin(steamInputType, strategy, handle, actionSet)
            ? ControllerConnectionState.Ready
            : ControllerConnectionState.Binding;
    }

    private static bool HasActiveActionOrigin(
        Type steamInputType,
        object strategy,
        object handle,
        object actionSet)
    {
        var catalog = ReadDigitalActionCatalog(strategy);
        if (catalog.Count == 0)
        {
            return false;
        }

        var originsMethod = steamInputType.GetMethods(Members)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "GetDigitalActionOrigins", StringComparison.Ordinal)
                && method.GetParameters() is
                [var handleParameter, var actionSetParameter, var actionParameter, var originsParameter]
                && handleParameter.ParameterType.IsAssignableFrom(handle.GetType())
                && actionSetParameter.ParameterType.IsAssignableFrom(actionSet.GetType())
                && originsParameter.ParameterType.IsArray);
        var originType = originsMethod?.GetParameters()[3].ParameterType.GetElementType();
        if (originsMethod is null || originType is null)
        {
            return false;
        }

        foreach (var entry in catalog)
        {
            if (!originsMethod.GetParameters()[2].ParameterType.IsAssignableFrom(entry.ActionHandle.GetType()))
            {
                continue;
            }

            var origins = Array.CreateInstance(originType, ReadSteamConstant("STEAM_INPUT_MAX_ORIGINS"));
            if (originsMethod.Invoke(null, [handle, actionSet, entry.ActionHandle, origins]) is int count && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<DigitalActionCatalogEntry> ReadDigitalActionCatalog(object strategy)
    {
        if (strategy.GetType().GetField("_inputEvents", Members)?.GetValue(strategy) is not IDictionary inputEvents
            || strategy.GetType().GetField("_digitalActionHandleCache", Members)?.GetValue(strategy) is not IDictionary actionHandles)
        {
            return [];
        }

        var catalog = new List<DigitalActionCatalogEntry>(inputEvents.Count);
        foreach (DictionaryEntry inputEvent in inputEvents)
        {
            if (inputEvent.Key is null || inputEvent.Value is null || !actionHandles.Contains(inputEvent.Key))
            {
                continue;
            }

            var actionHandle = actionHandles[inputEvent.Key];
            var actionId = GetProperty(inputEvent.Value, "Action")?.ToString();
            if (actionHandle is not null && !string.IsNullOrWhiteSpace(actionId))
            {
                catalog.Add(new DigitalActionCatalogEntry(actionHandle, inputEvent.Value, actionId));
            }
        }

        return catalog;
    }

    private void AddNativeFallbackControllers(
        List<ControllerSourceDescriptor> result,
        IReadOnlyList<object> steamHandles,
        int expectedControllerCount,
        string nativeSourceScope,
        bool steamProbeSucceeded,
        DateTimeOffset now)
    {
        var inputType = AccessTools.TypeByName("Godot.Input");
        var devices = inputType?.GetMethod("GetConnectedJoypads", Members, Type.EmptyTypes)?.Invoke(null, null) as IEnumerable;
        if (devices is null)
        {
            return;
        }

        var candidates = new List<ControllerSourceDescriptor>();
        foreach (var device in devices.Cast<object>().OfType<int>())
        {
            var info = ReadJoyInfo(inputType!, device);
            var serial = ReadInfoString(info, "serial_number");
            var vendor = ReadInfoInt(info, "vendor_id");
            var product = ReadInfoInt(info, "product_id");
            var rawName = ReadInfoString(info, "raw_name");
            var sourceIdentity = !string.IsNullOrWhiteSpace(serial)
                ? $"serial:{serial}"
                : $"collector:{nativeSourceScope}:device:{device}";
            var sourceId = $"godot:{sourceIdentity}";
            var isCommitted = _committedNativeSources.ContainsKey(sourceId);
            var steamInputIndex = ReadInfoInt(info, "steam_input_index");
            if (!isCommitted
                && steamHandles.Count > 0
                && steamInputIndex is not null
                && InvokeSteam("GetControllerForGamepadIndex", [steamInputIndex.Value]) is { } mappedHandle
                && ReadNumericHandle(mappedHandle) != 0
                && steamHandles.Any(handle => ReadNumericHandle(handle) == ReadNumericHandle(mappedHandle)))
            {
                continue;
            }

            var xInputIndex = ReadInfoInt(info, "xinput_index");
            if (!isCommitted && xInputIndex is not null && steamHandles.Any(handle =>
                    InvokeSteam("GetGamepadIndexForController", [handle]) is int index && index == xInputIndex.Value))
            {
                continue;
            }

            candidates.Add(new ControllerSourceDescriptor(
                sourceId,
                ControllerSourceKind.Godot,
                SteamHandle: null,
                ControllerType: "Godot",
                GodotDeviceId: device,
                SteamInputIndex: steamInputIndex,
                XInputIndex: xInputIndex,
                VendorId: vendor,
                ProductId: product,
                SerialNumber: serial,
                RawName: rawName));
        }

        var candidateIds = candidates.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var disconnected in _committedNativeSources.Keys.Where(sourceId => !candidateIds.Contains(sourceId)).ToArray())
        {
            _committedNativeSources.Remove(disconnected);
        }

        var existingSourceIds = result.Select(controller => controller.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var committed in candidates.Where(candidate => _committedNativeSources.ContainsKey(candidate.SourceId)))
        {
            _committedNativeSources[committed.SourceId] = committed;
            if (existingSourceIds.Add(committed.SourceId))
            {
                result.Add(committed);
            }
        }

        var deficit = Math.Clamp(expectedControllerCount - result.Count, 0, 4);
        if (deficit == 0)
        {
            NativeFallbackStatus = _committedNativeSources.Count > 0
                ? $"committed={_committedNativeSources.Count} steam={steamHandles.Count}"
                : "not-needed";
            return;
        }

        var fallbackReady = _committedNativeSources.Count > 0
            || !steamProbeSucceeded
            || steamHandles.Count > 0
            || _catalogInitialized
            || _nativeFallbackDelay <= TimeSpan.Zero;
        if (!fallbackReady)
        {
            _nativeFallbackObservedAt ??= now;
            var elapsed = now - _nativeFallbackObservedAt.Value;
            fallbackReady = elapsed >= _nativeFallbackDelay;
            if (!fallbackReady)
            {
                NativeFallbackStatus = $"waiting-for-steam delayMs={_nativeFallbackDelay.TotalMilliseconds:F0} candidates={candidates.Count}";
                return;
            }
        }

        foreach (var candidate in candidates.Where(candidate => !existingSourceIds.Contains(candidate.SourceId)))
        {
            _committedNativeSources[candidate.SourceId] = candidate;
            existingSourceIds.Add(candidate.SourceId);
            result.Add(candidate);
            if (--deficit == 0)
            {
                break;
            }
        }

        NativeFallbackStatus = $"committed={_committedNativeSources.Count} steam={steamHandles.Count} reason={(steamProbeSucceeded ? "steam-deficit" : "steam-unavailable")}";
    }

    private static IDictionary? ReadJoyInfo(Type inputType, int device)
    {
        return inputType.GetMethod("GetJoyInfo", Members, [typeof(int)])?.Invoke(null, [device]) as IDictionary;
    }

    private static int? ReadInfoInt(IDictionary? info, string key)
    {
        var value = ReadInfoValue(info, key);
        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(null);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        return null;
    }

    private static string? ReadInfoString(IDictionary? info, string key)
    {
        return ReadInfoValue(info, key)?.ToString();
    }

    private static object? ReadInfoValue(IDictionary? info, string key)
    {
        if (info is null)
        {
            return null;
        }

        foreach (DictionaryEntry entry in info)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static bool InputEventMatchesAction(object inputEvent, string actionId)
    {
        var method = inputEvent.GetType().GetMethods(Members)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "IsAction", StringComparison.Ordinal)
                && candidate.GetParameters().Length is 1 or 2);
        if (method is null)
        {
            return false;
        }

        var parameters = method.GetParameters();
        var action = ConvertActionName(actionId, parameters[0].ParameterType);
        if (action is null)
        {
            return false;
        }

        var arguments = parameters.Length == 1 ? new[] { action } : new[] { action, (object)false };
        return method.Invoke(inputEvent, arguments) is true;
    }

    private static IReadOnlyList<string> GetMatchingGodotActions(object inputEvent)
    {
        var inputMapType = AccessTools.TypeByName("Godot.InputMap")
            ?? throw new MissingMemberException("Godot.InputMap");
        var getActions = inputMapType.GetMethods(Members)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "GetActions", StringComparison.Ordinal)
                && candidate.GetParameters().Length == 0)
            ?? throw new MissingMethodException(inputMapType.FullName, "GetActions");
        if (getActions.Invoke(null, null) is not IEnumerable actions)
        {
            return [];
        }

        var eventIsAction = inputMapType.GetMethods(Members)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "EventIsAction", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length is 2 or 3
                    && parameters[0].ParameterType.IsAssignableFrom(inputEvent.GetType());
            })
            ?? throw new MissingMethodException(inputMapType.FullName, "EventIsAction");
        var parameters = eventIsAction.GetParameters();
        var matches = new List<string>();
        foreach (var action in actions)
        {
            var actionId = action?.ToString();
            if (string.IsNullOrWhiteSpace(actionId))
            {
                continue;
            }

            var convertedAction = ConvertActionName(actionId, parameters[1].ParameterType);
            if (convertedAction is null)
            {
                continue;
            }

            var arguments = parameters.Length == 2
                ? new[] { inputEvent, convertedAction }
                : new[] { inputEvent, convertedAction, (object)true };
            if (eventIsAction.Invoke(null, arguments) is true)
            {
                matches.Add(actionId);
            }
        }

        return matches;
    }

    private static void ReleaseGodotAction(string actionId)
    {
        var inputType = AccessTools.TypeByName("Godot.Input")
            ?? throw new MissingMemberException("Godot.Input");
        var method = inputType.GetMethods(Members)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "ActionRelease", StringComparison.Ordinal)
                && candidate.GetParameters().Length == 1)
            ?? throw new MissingMethodException(inputType.FullName, "ActionRelease");
        var action = ConvertActionName(actionId, method.GetParameters()[0].ParameterType);
        if (action is not null)
        {
            method.Invoke(null, [action]);
        }
    }

    private static float ReadInputActionStrength(object inputEvent, string actionId)
    {
        var method = inputEvent.GetType().GetMethods(Members)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "GetActionStrength", StringComparison.Ordinal)
                && candidate.GetParameters().Length is 1 or 2);
        if (method is null)
        {
            return 0;
        }

        var parameters = method.GetParameters();
        var action = ConvertActionName(actionId, parameters[0].ParameterType);
        if (action is null)
        {
            return 0;
        }

        var arguments = parameters.Length == 1 ? new[] { action } : new[] { action, (object)false };
        return method.Invoke(inputEvent, arguments) is IConvertible value ? value.ToSingle(null) : 0;
    }

    private static object? ConvertActionName(string actionName, Type targetType)
    {
        if (targetType == typeof(string) || targetType.IsAssignableFrom(typeof(string)))
        {
            return actionName;
        }

        return targetType.GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public, [typeof(string)])
                   ?.Invoke(null, [actionName])
            ?? targetType.GetConstructor([typeof(string)])?.Invoke([actionName]);
    }

    private static object? GetActionSetHandle(object strategy)
    {
        var actionSet = strategy.GetType().GetField("_currentActionSetHandle", Members)?.GetValue(strategy);
        if (actionSet is not null && ReadNumericHandle(actionSet) != 0)
        {
            return actionSet;
        }

        actionSet = InvokeSteam("GetActionSetHandle", ["Controls"]);
        if (actionSet is not null)
        {
            strategy.GetType().GetField("_currentActionSetHandle", Members)?.SetValue(strategy, actionSet);
        }

        return actionSet;
    }

    private static void RunSteamInputFrame()
    {
        InvokeSteam("RunFrame", [true]);
    }

    private static int ReadSteamConstant(string name)
    {
        var constantsType = AccessTools.TypeByName("Steamworks.Constants")
            ?? throw new MissingMemberException("Steamworks.Constants");
        var value = constantsType.GetField(name, Members)?.GetValue(null)
            ?? throw new MissingFieldException(constantsType.FullName, name);
        var result = Convert.ToInt32(value);
        return result > 0
            ? result
            : throw new InvalidOperationException($"Steamworks constant {name} must be positive.");
    }

    private static object? InvokeSteam(string methodName, object?[] arguments)
    {
        var type = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        var method = type.GetMethods(Members)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                if (parameters.Length != arguments.Length)
                {
                    return false;
                }

                return parameters.Select((parameter, index) =>
                        arguments[index] is null || parameter.ParameterType.IsInstanceOfType(arguments[index]))
                    .All(matches => matches);
            })
            ?? throw new MissingMethodException(type.FullName, methodName);
        return method.Invoke(null, arguments);
    }

    private static ulong ReadNumericHandle(object handle)
    {
        foreach (var name in new[] { "m_InputHandle", "m_InputActionSetHandle", "m_InputDigitalActionHandle", "m_InputAnalogActionHandle", "Value" })
        {
            var value = handle.GetType().GetField(name, Members)?.GetValue(handle)
                ?? handle.GetType().GetProperty(name, Members)?.GetValue(handle);
            if (value is IConvertible convertible)
            {
                return convertible.ToUInt64(null);
            }
        }

        return handle is IConvertible direct ? direct.ToUInt64(null) : 0;
    }

    private static object? CreateHandle(Type handleType, ulong value)
    {
        var constructor = handleType.GetConstructor([typeof(ulong)]);
        if (constructor is not null)
        {
            return constructor.Invoke([value]);
        }

        var handle = Activator.CreateInstance(handleType);
        foreach (var name in new[] { "m_InputHandle", "Value" })
        {
            var field = handleType.GetField(name, Members);
            if (field is not null)
            {
                field.SetValue(handle, Convert.ChangeType(value, field.FieldType));
                return handle;
            }
        }

        return null;
    }

    private static object? Duplicate(object source)
    {
        return source.GetType().GetMethod("Duplicate", Members, [typeof(bool)])?.Invoke(source, [false])
            ?? source.GetType().GetMethod("Duplicate", Members, Type.EmptyTypes)?.Invoke(source, null);
    }

    private static object? CreateDigitalInputEvent(string actionId)
    {
        var type = AccessTools.TypeByName("Godot.InputEventAction");
        var inputEvent = type is null ? null : Activator.CreateInstance(type);
        if (inputEvent is not null)
        {
            SetProperty(inputEvent, "Action", actionId);
        }

        return inputEvent;
    }

    private static void SetProperty(object source, string propertyName, object value)
    {
        var property = source.GetType().GetProperty(propertyName, Members);
        if (property?.CanWrite is true)
        {
            var converted = property.PropertyType.IsInstanceOfType(value)
                ? value
                : value is string text
                    ? ConvertActionName(text, property.PropertyType)
                    : Convert.ChangeType(value, property.PropertyType);
            property.SetValue(source, converted);
        }
    }

    private static object? GetProperty(object? source, string propertyName)
    {
        return source?.GetType().GetProperty(propertyName, Members)?.GetValue(source);
    }

    private static string FormatSteamFailure(Exception exception)
    {
        var leaf = exception;
        while (leaf is TargetInvocationException { InnerException: not null } invocation)
        {
            leaf = invocation.InnerException!;
        }

        return $"{leaf.GetType().Name}: {leaf.Message}";
    }

    private static bool ReadBool(object? source, params string[] names)
    {
        foreach (var name in names)
        {
            var value = source?.GetType().GetField(name, Members)?.GetValue(source)
                ?? source?.GetType().GetProperty(name, Members)?.GetValue(source);
            if (value is bool boolean)
            {
                return boolean;
            }

            if (value is byte unsignedByte)
            {
                return unsignedByte != 0;
            }

            if (value is sbyte signedByte)
            {
                return signedByte != 0;
            }
        }

        return false;
    }

    private static float ReadFloat(object? source, params string[] names)
    {
        foreach (var name in names)
        {
            var value = source?.GetType().GetField(name, Members)?.GetValue(source)
                ?? source?.GetType().GetProperty(name, Members)?.GetValue(source);
            if (value is IConvertible convertible)
            {
                return convertible.ToSingle(null);
            }
        }

        return 0;
    }

    private static void ParseInputEvent(object inputEvent)
    {
        var inputType = AccessTools.TypeByName("Godot.Input")
            ?? throw new MissingMemberException("Godot.Input");
        var method = inputType.GetMethods(Members)
            .First(candidate => string.Equals(candidate.Name, "ParseInputEvent", StringComparison.Ordinal)
                && candidate.GetParameters() is [var parameter]
                && parameter.ParameterType.IsAssignableFrom(inputEvent.GetType()));
        method.Invoke(null, [inputEvent]);
    }
}

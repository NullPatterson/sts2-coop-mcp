using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace LocalCoop.Mod.Runtime;

public static class SteamControllerInputSelection
{
    private static readonly BindingFlags Members =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly object Lock = new();
    private static readonly HashSet<object> GeneratedInputEvents = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<object> AcceptedUiCompanionInputEvents = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<object> AcceptedOriginalSteamControllerInputEvents = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<object> AcceptedNativeGeneratedInputEvents = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, Queue<DateTimeOffset>> PendingUiCompanionActions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Queue<DateTimeOffset>> PendingOriginalSteamControllerInputs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Queue<DateTimeOffset>> PendingNativeGeneratedActions = new(StringComparer.Ordinal);
    private static readonly Queue<NativeFallbackGeneratedActionToken> PendingNativeFallbackGeneratedActions = new();
    private static readonly Dictionary<string, string> NativeGeneratedActionMap = CreateFallbackNativeGeneratedActionMap();
    private static readonly TimeSpan UiCompanionTokenLifetime = TimeSpan.FromMilliseconds(250);
    private const string FallbackTopPanelSourceAction = "controller_face_button_west";
    private const string FallbackTopPanelNativeAction = "mega_top_panel";
    private const int SteamInputMaxOrigins = 8;
    private static string? _lastSelectionSummary;
    private static int? _selectedControllerDevice;
    private static int? _knownControllerDevice;
    private static object? _knownControllerHandle;

    public static SteamControllerHandleSelection<T> ChooseControllerHandle<T>(
        IReadOnlyList<T> handles,
        BrokerControllerDeviceAssignment assignment)
    {
        return ChooseConfiguredControllerHandle(handles, assignment, unavailableControllerHandles: null);
    }

    private static SteamControllerHandleSelection<T> ChooseConfiguredControllerHandle<T>(
        IReadOnlyList<T> handles,
        BrokerControllerDeviceAssignment assignment,
        IReadOnlySet<T>? unavailableControllerHandles)
    {
        if (!assignment.IsConfigured || assignment.Device is null)
        {
            return new SteamControllerHandleSelection<T>(
                Selected: false,
                Index: -1,
                Handle: default,
                Reason: "controllerDevice is unconfigured or none");
        }

        var index = assignment.Device.Value;
        if (index < 0 || index >= handles.Count)
        {
            return new SteamControllerHandleSelection<T>(
                Selected: false,
                Index: index,
                Handle: default,
                Reason: $"no connected Steam controller at playerSlot={index}");
        }

        var handle = handles[index];
        if (unavailableControllerHandles?.Contains(handle) is true)
        {
            return new SteamControllerHandleSelection<T>(
                Selected: false,
                Index: index,
                Handle: default,
                Reason: $"configured playerSlot={index} controller is already claimed");
        }

        return new SteamControllerHandleSelection<T>(
            Selected: true,
            Index: index,
            Handle: handle,
            Reason: $"selected playerSlot={index}");
    }

    public static SteamControllerHandleSelection<T> ChooseControllerHandle<T>(
        IReadOnlyList<T> handles,
        BrokerControllerDeviceAssignment assignment,
        T? knownControllerHandle,
        int? controllerClientCount = null,
        IReadOnlySet<T>? unavailableControllerHandles = null)
    {
        if (!assignment.IsConfigured || assignment.Device is null)
        {
            return new SteamControllerHandleSelection<T>(
                Selected: false,
                Index: -1,
                Handle: default,
                Reason: "controllerDevice is unconfigured or none");
        }

        var index = assignment.Device.Value;
        if (knownControllerHandle is null)
        {
            return ChooseConfiguredControllerHandle(handles, assignment, unavailableControllerHandles);
        }

        foreach (var handle in handles)
        {
            if (EqualityComparer<T>.Default.Equals(handle, knownControllerHandle)
                && unavailableControllerHandles?.Contains(handle) is not true)
            {
                return new SteamControllerHandleSelection<T>(
                    Selected: true,
                    Index: index,
                    Handle: handle,
                    Reason: $"retained previous selected Steam controller handle for playerSlot={index}");
            }
        }

        if (controllerClientCount is not > 0)
        {
            var fallback = ChooseConfiguredControllerHandle(handles, assignment, unavailableControllerHandles);
            return fallback with
            {
                Reason = fallback.Selected
                    ? $"previous selected Steam controller handle is disconnected; reacquired configured playerSlot={index}"
                    : $"previous selected Steam controller handle is disconnected; {fallback.Reason}"
            };
        }

        var requiredControllers = controllerClientCount.Value;
        if (handles.Count <= requiredControllers)
        {
            return new SteamControllerHandleSelection<T>(
                Selected: false,
                Index: index,
                Handle: default,
                Reason: $"previous selected Steam controller handle is disconnected; no spare controller connected count={handles.Count} controllerClients={requiredControllers}");
        }

        for (var spareIndex = requiredControllers; spareIndex < handles.Count; spareIndex++)
        {
            var spareHandle = handles[spareIndex];
            if (unavailableControllerHandles?.Contains(spareHandle) is true)
            {
                continue;
            }

            return new SteamControllerHandleSelection<T>(
                Selected: true,
                Index: index,
                Handle: spareHandle,
                Reason: $"previous selected Steam controller handle is disconnected; assigned spare controller handleIndex={spareIndex} controllerClients={requiredControllers}",
                RememberHandle: false);
        }

        return new SteamControllerHandleSelection<T>(
            Selected: false,
            Index: index,
            Handle: default,
            Reason: $"previous selected Steam controller handle is disconnected; spare controller already claimed connected count={handles.Count} controllerClients={requiredControllers}");
    }

    public static bool IsGeneratedInputEvent(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return false;
        }

        lock (Lock)
        {
            return GeneratedInputEvents.Contains(inputEvent);
        }
    }

    public static bool IsSelectedControllerActive(BrokerControllerDeviceAssignment assignment)
    {
        if (!assignment.IsConfigured || assignment.Device is null)
        {
            return false;
        }

        lock (Lock)
        {
            return _selectedControllerDevice == assignment.Device.Value;
        }
    }

    public static void RegisterGeneratedInputEvents(IEnumerable<object?> inputEvents)
    {
        lock (Lock)
        {
            foreach (var inputEvent in inputEvents)
            {
                if (inputEvent is not null)
                {
                    GeneratedInputEvents.Add(inputEvent);
                }
            }
        }
    }

    public static void RegisterGeneratedUiCompanionAction(object? inputEvent, DateTimeOffset? now = null)
    {
        if (!CanCreateUiCompanionAction(inputEvent))
        {
            return;
        }

        var companionAction = MapSteamControllerActionToUiCompanion(GetActionName(inputEvent));
        if (companionAction is null)
        {
            return;
        }

        lock (Lock)
        {
            if (!PendingUiCompanionActions.TryGetValue(companionAction, out var pendingTokens))
            {
                pendingTokens = new Queue<DateTimeOffset>();
                PendingUiCompanionActions[companionAction] = pendingTokens;
            }

            pendingTokens.Enqueue(now ?? DateTimeOffset.UtcNow);
        }
    }

    public static bool CanCreateUiCompanionAction(object? inputEvent)
    {
        return IsPressedInput(inputEvent)
            && MapSteamControllerActionToUiCompanion(GetActionName(inputEvent)) is not null;
    }

    public static bool CanMapUiCompanionAction(object? inputEvent)
    {
        return MapSteamControllerActionToUiCompanion(GetActionName(inputEvent)) is not null;
    }

    public static bool CanMapNativeGeneratedAction(object? inputEvent)
    {
        return MapSteamControllerActionToNativeGeneratedAction(GetActionName(inputEvent)) is not null;
    }

    public static bool CanTrustOriginalSteamControllerInput(object? inputEvent)
    {
        if (inputEvent is null || CanMapUiCompanionAction(inputEvent) || CanMapNativeGeneratedAction(inputEvent))
        {
            return false;
        }

        var action = GetActionName(inputEvent);
        if (action is not null)
        {
            return action.StartsWith("controller_", StringComparison.Ordinal);
        }

        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        return typeName.Contains("JoypadMotion", StringComparison.OrdinalIgnoreCase);
    }

    public static void RegisterGeneratedNativeAction(object? inputEvent, DateTimeOffset? now = null)
    {
        if (!IsPressedInput(inputEvent) || CanMapUiCompanionAction(inputEvent))
        {
            return;
        }

        var nativeAction = MapSteamControllerActionToNativeGeneratedAction(GetActionName(inputEvent));
        if (nativeAction is null)
        {
            return;
        }

        lock (Lock)
        {
            if (!PendingNativeGeneratedActions.TryGetValue(nativeAction, out var pendingTokens))
            {
                pendingTokens = new Queue<DateTimeOffset>();
                PendingNativeGeneratedActions[nativeAction] = pendingTokens;
            }

            pendingTokens.Enqueue(now ?? DateTimeOffset.UtcNow);
        }
    }

    public static bool TryConsumeGeneratedNativeInputEvent(object? inputEvent, DateTimeOffset? now = null)
    {
        if (inputEvent is null)
        {
            return false;
        }

        lock (Lock)
        {
            if (AcceptedNativeGeneratedInputEvents.Contains(inputEvent))
            {
                return true;
            }
        }

        if (!IsPressedInput(inputEvent))
        {
            return false;
        }

        var action = GetActionName(inputEvent);
        if (action is null)
        {
            return false;
        }

        lock (Lock)
        {
            if (AcceptedNativeGeneratedInputEvents.Contains(inputEvent))
            {
                return true;
            }

            var currentTime = now ?? DateTimeOffset.UtcNow;
            if (!PendingNativeGeneratedActions.TryGetValue(action, out var pendingTokens))
            {
                return false;
            }

            PruneExpiredNativeGeneratedActionTokens(action, pendingTokens, currentTime);
            if (pendingTokens.Count == 0)
            {
                return false;
            }

            pendingTokens.Dequeue();
            if (pendingTokens.Count == 0)
            {
                PendingNativeGeneratedActions.Remove(action);
            }

            AcceptedNativeGeneratedInputEvents.Add(inputEvent);
            return true;
        }
    }

    public static void RegisterNativeFallbackSourceInput(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        DateTimeOffset? now = null)
    {
        if (!IsNativeJoypadButton(inputEvent)
            || !DeviceMatches(inputEvent, assignment))
        {
            return;
        }

        lock (Lock)
        {
            PendingNativeFallbackGeneratedActions.Enqueue(new NativeFallbackGeneratedActionToken(
                now ?? DateTimeOffset.UtcNow,
                GetPressedState(inputEvent) ?? true));
        }
    }

    public static bool TryConsumeNativeFallbackGeneratedInput(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        DateTimeOffset? now = null)
    {
        if (!IsGeneratedActionFromNativeFallback(inputEvent, assignment))
        {
            return false;
        }

        lock (Lock)
        {
            var currentTime = now ?? DateTimeOffset.UtcNow;
            while (PendingNativeFallbackGeneratedActions.Count > 0
                   && currentTime - PendingNativeFallbackGeneratedActions.Peek().Timestamp > UiCompanionTokenLifetime)
            {
                PendingNativeFallbackGeneratedActions.Dequeue();
            }

            var pressed = GetPressedState(inputEvent) ?? true;
            if (PendingNativeFallbackGeneratedActions.Count == 0
                || PendingNativeFallbackGeneratedActions.Peek().Pressed != pressed)
            {
                return false;
            }

            PendingNativeFallbackGeneratedActions.Dequeue();
            return true;
        }
    }

    public static void RegisterGeneratedOriginalSteamControllerInput(object? inputEvent, DateTimeOffset? now = null)
    {
        var key = GetOriginalSteamControllerInputKey(inputEvent);
        if (key is null)
        {
            return;
        }

        lock (Lock)
        {
            if (!PendingOriginalSteamControllerInputs.TryGetValue(key, out var pendingTokens))
            {
                pendingTokens = new Queue<DateTimeOffset>();
                PendingOriginalSteamControllerInputs[key] = pendingTokens;
            }

            pendingTokens.Enqueue(now ?? DateTimeOffset.UtcNow);
        }
    }

    public static bool TryConsumeGeneratedOriginalSteamControllerInput(object? inputEvent, DateTimeOffset? now = null)
    {
        if (inputEvent is null)
        {
            return false;
        }

        var key = GetOriginalSteamControllerInputKey(inputEvent);
        if (key is null)
        {
            return false;
        }

        lock (Lock)
        {
            if (AcceptedOriginalSteamControllerInputEvents.Contains(inputEvent))
            {
                return true;
            }

            var currentTime = now ?? DateTimeOffset.UtcNow;
            if (!PendingOriginalSteamControllerInputs.TryGetValue(key, out var pendingTokens))
            {
                return false;
            }

            PruneExpiredOriginalSteamControllerInputTokens(key, pendingTokens, currentTime);
            if (pendingTokens.Count == 0)
            {
                return false;
            }

            pendingTokens.Dequeue();
            if (pendingTokens.Count == 0)
            {
                PendingOriginalSteamControllerInputs.Remove(key);
            }

            AcceptedOriginalSteamControllerInputEvents.Add(inputEvent);
            return true;
        }
    }

    public static bool TryCreateUiCompanionInputEvent(object? inputEvent, out object? uiCompanionInputEvent)
    {
        uiCompanionInputEvent = null;
        if (!CanMapUiCompanionAction(inputEvent) || inputEvent is null)
        {
            return false;
        }

        var companionAction = MapSteamControllerActionToUiCompanion(GetActionName(inputEvent));
        if (companionAction is null)
        {
            return false;
        }

        var duplicate = TryDuplicateInputEvent(inputEvent);
        if (duplicate is null || !TrySetActionName(duplicate, companionAction))
        {
            return false;
        }

        uiCompanionInputEvent = duplicate;
        return true;
    }

    public static bool TryCreateMappedInputEvent(
        object? inputEvent,
        out object? mappedInputEvent,
        out string? targetAction)
    {
        mappedInputEvent = null;
        targetAction = GetMappedTargetAction(inputEvent);
        if (targetAction is null || inputEvent is null)
        {
            return false;
        }

        var duplicate = TryDuplicateInputEvent(inputEvent);
        if (duplicate is null || !TrySetActionName(duplicate, targetAction))
        {
            return false;
        }

        mappedInputEvent = duplicate;
        return true;
    }

    public static string? GetMappedTargetAction(object? inputEvent)
    {
        var action = GetActionName(inputEvent);
        return MapSteamControllerActionToUiCompanion(action)
            ?? MapSteamControllerActionToNativeGeneratedAction(action);
    }

    public static void AcceptGeneratedMappedInputEvent(object? inputEvent, string targetAction)
    {
        if (targetAction.StartsWith("ui_", StringComparison.Ordinal))
        {
            AcceptGeneratedUiCompanionInputEvent(inputEvent);
            return;
        }

        AcceptGeneratedNativeInputEvent(inputEvent);
    }

    public static bool TryDispatchUiCompanionInputEvent(object? inputEvent)
    {
        if (!TryCreateUiCompanionInputEvent(inputEvent, out var uiCompanionInputEvent)
            || uiCompanionInputEvent is null)
        {
            return false;
        }

        try
        {
            var inputType = AccessTools.TypeByName("Godot.Input");
            var parseInputEvent = inputType?.GetMethods(Members)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "ParseInputEvent", StringComparison.Ordinal)
                    && method.GetParameters() is [var parameter]
                    && parameter.ParameterType.IsAssignableFrom(uiCompanionInputEvent.GetType()));
            if (parseInputEvent is null)
            {
                return false;
            }

            AcceptGeneratedUiCompanionInputEvent(uiCompanionInputEvent);
            parseInputEvent.Invoke(null, [uiCompanionInputEvent]);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryConsumeGeneratedUiCompanionInputEvent(object? inputEvent, DateTimeOffset? now = null)
    {
        if (inputEvent is null)
        {
            return false;
        }

        lock (Lock)
        {
            if (AcceptedUiCompanionInputEvents.Contains(inputEvent))
            {
                return true;
            }
        }

        if (!IsPressedInput(inputEvent))
        {
            return false;
        }

        var action = GetActionName(inputEvent);
        if (action is null)
        {
            return false;
        }

        lock (Lock)
        {
            if (AcceptedUiCompanionInputEvents.Contains(inputEvent))
            {
                return true;
            }

            var currentTime = now ?? DateTimeOffset.UtcNow;
            if (!PendingUiCompanionActions.TryGetValue(action, out var pendingTokens))
            {
                return false;
            }

            PruneExpiredUiCompanionTokens(action, pendingTokens, currentTime);
            if (pendingTokens.Count == 0)
            {
                return false;
            }

            pendingTokens.Dequeue();
            if (pendingTokens.Count == 0)
            {
                PendingUiCompanionActions.Remove(action);
            }

            AcceptedUiCompanionInputEvents.Add(inputEvent);
            return true;
        }
    }

    public static void AcceptGeneratedUiCompanionInputEventForTesting(object? inputEvent)
    {
        AcceptGeneratedUiCompanionInputEvent(inputEvent);
    }

    public static void ClearGeneratedInputEventsForTesting()
    {
        lock (Lock)
        {
            GeneratedInputEvents.Clear();
            AcceptedUiCompanionInputEvents.Clear();
            AcceptedOriginalSteamControllerInputEvents.Clear();
            AcceptedNativeGeneratedInputEvents.Clear();
            PendingUiCompanionActions.Clear();
            PendingOriginalSteamControllerInputs.Clear();
            PendingNativeGeneratedActions.Clear();
            PendingNativeFallbackGeneratedActions.Clear();
            ResetNativeGeneratedActionMap();
            _lastSelectionSummary = null;
            _selectedControllerDevice = null;
            _knownControllerDevice = null;
            _knownControllerHandle = null;
        }
    }

    public static void SetSelectedControllerDeviceForTesting(int? controllerDevice)
    {
        lock (Lock)
        {
            _selectedControllerDevice = controllerDevice;
        }
    }

    public static IReadOnlyDictionary<string, string> CreateNativeGeneratedActionMapForTesting(object? controllerConfig)
    {
        return CreateNativeGeneratedActionMap(controllerConfig);
    }

    public static void SetNativeGeneratedActionMapForTesting(IReadOnlyDictionary<string, string> nativeGeneratedActionMap)
    {
        SetNativeGeneratedActionMap(nativeGeneratedActionMap);
    }

    public static bool IsSelectionAlreadyAppliedForTesting(
        BrokerControllerDeviceAssignment assignment,
        object? currentHandle,
        object? selectedHandle)
    {
        return IsSelectionAlreadyApplied(assignment, currentHandle, selectedHandle);
    }

    public static BrokerControllerDeviceAssignment ResolveNativeFallbackAssignmentForTesting(
        BrokerControllerDeviceAssignment assignment,
        int? controllerClientCount)
    {
        return ResolveNativeFallbackAssignment(assignment, controllerClientCount);
    }

    private static void AcceptGeneratedUiCompanionInputEvent(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return;
        }

        lock (Lock)
        {
            AcceptedUiCompanionInputEvents.Add(inputEvent);
        }
    }

    private static void AcceptGeneratedNativeInputEvent(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return;
        }

        lock (Lock)
        {
            AcceptedNativeGeneratedInputEvents.Add(inputEvent);
        }
    }

    public static void ApplySelection(
        object strategy,
        BrokerControllerDeviceAssignment assignment,
        int? controllerClientCount,
        string claimScope,
        int clientIndex,
        Action<string> log)
    {
        if (!assignment.IsConfigured)
        {
            ClearSelectedControllerIdentity();
            return;
        }

        try
        {
            if (assignment.Device is null)
            {
                ClearGeneratedInputEvents();
                ClearSelectedControllerIdentity();
                ClearCurrentControllerHandle(strategy);
                ClearPressedInputs(strategy);
                return;
            }

            RunSteamInputFrame();
            var handles = GetConnectedControllerHandles(strategy);
            var handleSummary = DescribeSteamControllerHandles(handles);
            var gamepadHandleSummary = DescribeSteamGamepadIndexHandles(
                strategy,
                handles,
                controllerClientCount);
            var knownHandle = GetKnownControllerHandle(assignment);
            var unavailableHandles = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var selection = ChooseControllerHandle(
                handles,
                assignment,
                knownHandle,
                controllerClientCount,
                unavailableHandles);

            if (!selection.Selected || selection.Handle is null)
            {
                var nativeFallbackAssignment = ResolveNativeFallbackAssignment(assignment, controllerClientCount);
                var nativeJoypads = GetConnectedNativeJoypadDeviceIds();
                ClearGeneratedInputEvents();
                ClearSelectedControllerDevice();
                ClearCurrentControllerHandle(strategy);
                ClearPressedInputs(strategy);
                LogIfChanged(
                    $"Steam controller selection: unavailable playerSlot={selection.Index} connected={handles.Count} "
                    + $"controllerClients={controllerClientCount?.ToString() ?? "<unknown>"} reason={selection.Reason}"
                    + $" nativeFallback={FormatNativeFallback(nativeFallbackAssignment)}"
                    + $" nativeJoypads={FormatNativeJoypads(nativeJoypads)}"
                    + $" handles={handleSummary}"
                    + $" gamepadSlots={gamepadHandleSummary}"
                    + ".",
                    log);
                return;
            }

            var previousHandle = GetCurrentControllerHandle(strategy);
            if (IsSelectionAlreadyApplied(assignment, previousHandle, selection.Handle))
            {
                RefreshSelectedInputStateForFrame(strategy);
                return;
            }

            SetCurrentControllerHandle(strategy, selection.Handle);
            var controllerType = RefreshControllerConfig(strategy, selection.Handle);
            var selectedNativeFallbackAssignment = ResolveNativeFallbackAssignment(assignment, controllerClientCount);
            var selectedNativeJoypads = GetConnectedNativeJoypadDeviceIds();
            var bindingAvailability = EvaluateSteamControllerBindings(
                selection.Handle,
                GetCurrentActionSetHandle(strategy));
            if (bindingAvailability.IsPending)
            {
                ClearGeneratedInputEvents();
                ClearSelectedControllerDevice();
                ClearCurrentControllerHandle(strategy);
                ClearPressedInputs(strategy);
                LogIfChanged(
                    "Steam controller selection: "
                    + $"pending playerSlot={selection.Index} handle={selection.Handle} connected={handles.Count} "
                    + $"controllerClients={controllerClientCount?.ToString() ?? "<unknown>"} "
                    + $"reason={bindingAvailability.Summary} "
                    + $"nativeFallback={FormatNativeFallback(selectedNativeFallbackAssignment)} "
                    + $"nativeJoypads={FormatNativeJoypads(selectedNativeJoypads)} "
                    + $"handles={handleSummary} "
                    + $"gamepadSlots={gamepadHandleSummary}.",
                    log);
                return;
            }

            if (bindingAvailability.IsKnown && !bindingAvailability.IsUsable)
            {
                ClearGeneratedInputEvents();
                ClearSelectedControllerDevice();
                ClearCurrentControllerHandle(strategy);
                ClearPressedInputs(strategy);
                LogIfChanged(
                    "Steam controller selection: "
                    + $"unusable playerSlot={selection.Index} handle={selection.Handle} connected={handles.Count} "
                    + $"controllerClients={controllerClientCount?.ToString() ?? "<unknown>"} "
                    + $"reason={bindingAvailability.Summary} "
                    + $"nativeFallback={FormatNativeFallback(selectedNativeFallbackAssignment)} "
                    + $"nativeJoypads={FormatNativeJoypads(selectedNativeJoypads)} "
                    + $"handles={handleSummary} "
                    + $"gamepadSlots={gamepadHandleSummary}.",
                    log);
                return;
            }

            if (IsUnknownSteamInputType(controllerType)
                && !bindingAvailability.IsKnown
                && selectedNativeFallbackAssignment.Device is not null)
            {
                ClearGeneratedInputEvents();
                ClearSelectedControllerDevice();
                ClearCurrentControllerHandle(strategy);
                ClearPressedInputs(strategy);
                LogIfChanged(
                    "Steam controller selection: "
                    + $"unknown input type playerSlot={selection.Index} handle={selection.Handle} connected={handles.Count} "
                    + $"controllerClients={controllerClientCount?.ToString() ?? "<unknown>"} "
                    + $"reason={bindingAvailability.Summary} "
                    + $"nativeFallback={FormatNativeFallback(selectedNativeFallbackAssignment)} "
                    + $"nativeJoypads={FormatNativeJoypads(selectedNativeJoypads)} "
                    + $"handles={handleSummary} "
                    + $"gamepadSlots={gamepadHandleSummary}.",
                    log);
                return;
            }

            var nativeBridgeSummary = RefreshNativeGeneratedActionMap(strategy);
            RegisterGeneratedInputEventsFromStrategy(strategy);

            if (!Equals(previousHandle, selection.Handle))
            {
                ClearPressedInputs(strategy);
            }

            SetSelectedControllerDevice(selection.Index, selection.Handle, selection.RememberHandle);
            LogIfChanged(
                "Steam controller selection: "
                + $"selected playerSlot={selection.Index} handle={selection.Handle} connected={handles.Count} "
                + $"controllerClients={controllerClientCount?.ToString() ?? "<unknown>"} "
                + $"inputType={controllerType ?? "<unknown>"} {nativeBridgeSummary} "
                + $"nativeFallback={FormatNativeFallback(selectedNativeFallbackAssignment)} "
                + $"nativeJoypads={FormatNativeJoypads(selectedNativeJoypads)} "
                + $"handles={handleSummary} "
                + $"gamepadSlots={gamepadHandleSummary}.",
                log);
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            ClearGeneratedInputEvents();
            ClearSelectedControllerDevice();
            LogIfChanged(
                $"Steam controller selection failed: {exception.GetType().Name}: {exception.Message}",
                log);
        }
    }

    public static bool RefreshSelectedInputStateForFrame(object strategy)
    {
        try
        {
            var handle = GetCurrentControllerHandle(strategy);
            if (handle is null)
            {
                return false;
            }

            RunSteamInputFrame();
            var actionSet = GetCurrentActionSetHandle(strategy) ?? ResolveControlsActionSet(strategy);
            if (actionSet is null)
            {
                return true;
            }

            ActivateSteamInputActionSet(handle, actionSet);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static BrokerControllerDeviceAssignment ResolveEffectiveControllerDevice(
        BrokerControllerDeviceAssignment assignment,
        int? controllerClientCount)
    {
        return IsSelectedControllerActive(assignment)
            ? assignment
            : ResolveNativeFallbackAssignment(assignment, controllerClientCount);
    }

    private static bool IsSelectionAlreadyApplied(
        BrokerControllerDeviceAssignment assignment,
        object? currentHandle,
        object? selectedHandle)
    {
        if (!assignment.IsConfigured
            || assignment.Device is null
            || selectedHandle is null
            || !Equals(currentHandle, selectedHandle))
        {
            return false;
        }

        lock (Lock)
        {
            return _selectedControllerDevice == assignment.Device.Value;
        }
    }

    private static List<object> GetConnectedControllerHandles(object strategy)
    {
        var handleType = GetSteamInputHandleType(strategy);
        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        var method = steamInputType.GetMethod("GetConnectedControllers", Members, [handleType.MakeArrayType()])
            ?? throw new MissingMethodException("Steamworks.SteamInput", "GetConnectedControllers");

        var handlesArray = Array.CreateInstance(handleType, 16);
        var count = method.Invoke(null, [handlesArray]) is int connectedCount ? connectedCount : 0;
        var handles = new List<object>(Math.Max(count, 0));
        for (var index = 0; index < count && index < handlesArray.Length; index++)
        {
            var handle = handlesArray.GetValue(index);
            if (handle is not null)
            {
                handles.Add(handle);
            }
        }

        return handles;
    }

    private static Type GetSteamInputHandleType(object strategy)
    {
        var handleField = strategy.GetType().GetField("_currentControllerHandle", Members)
            ?? throw new MissingMemberException(strategy.GetType().FullName, "_currentControllerHandle");
        var nullableHandleType = handleField.FieldType;
        return Nullable.GetUnderlyingType(nullableHandleType) ?? nullableHandleType;
    }

    private static bool TryGetControllerHandleForGamepadIndex(
        object strategy,
        int gamepadIndex,
        out object? handle)
    {
        handle = null;
        try
        {
            var handleType = GetSteamInputHandleType(strategy);
            var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
                ?? throw new MissingMemberException("Steamworks.SteamInput");
            var method = steamInputType.GetMethod("GetControllerForGamepadIndex", Members, [typeof(int)]);
            if (method is null)
            {
                return false;
            }

            var mappedHandle = method.Invoke(null, [gamepadIndex]);
            if (mappedHandle is null || !handleType.IsInstanceOfType(mappedHandle))
            {
                return false;
            }

            handle = mappedHandle;
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryFindConnectedControllerHandle(
        IReadOnlyList<object> connectedHandles,
        object mappedHandle,
        out object? connectedHandle)
    {
        foreach (var handle in connectedHandles)
        {
            if (Equals(handle, mappedHandle))
            {
                connectedHandle = handle;
                return true;
            }
        }

        connectedHandle = null;
        return false;
    }

    private static BrokerControllerDeviceAssignment ResolveNativeFallbackAssignment(
        BrokerControllerDeviceAssignment assignment,
        int? controllerClientCount)
    {
        if (!assignment.IsConfigured || assignment.Device is null)
        {
            return assignment;
        }

        if (controllerClientCount is not > 0 || assignment.Device.Value >= controllerClientCount.Value)
        {
            return assignment;
        }

        var nativeJoypads = GetConnectedNativeJoypadDeviceIds();
        if (nativeJoypads.Count == 0)
        {
            return assignment;
        }

        var playerSlot = assignment.Device.Value;
        if (playerSlot < nativeJoypads.Count)
        {
            return new BrokerControllerDeviceAssignment(IsConfigured: true, Device: nativeJoypads[playerSlot]);
        }

        return BrokerControllerDeviceAssignment.None;
    }

    private static List<int> GetConnectedNativeJoypadDeviceIds()
    {
        try
        {
            var inputType = AccessTools.TypeByName("Godot.Input");
            var method = inputType?.GetMethod("GetConnectedJoypads", Members, Type.EmptyTypes);
            if (method?.Invoke(null, null) is not IEnumerable joypads)
            {
                return [];
            }

            var devices = new List<int>();
            foreach (var joypad in joypads)
            {
                if (TryConvertInt(joypad, out var device) && device >= 0)
                {
                    devices.Add(device);
                }
            }

            return devices;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    private static bool TryConvertInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case IConvertible convertible:
                try
                {
                    result = convertible.ToInt32(null);
                    return true;
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
                {
                    result = default;
                    return false;
                }
            default:
                result = default;
                return false;
        }
    }

    private static string FormatNativeFallback(BrokerControllerDeviceAssignment assignment)
    {
        if (!assignment.IsConfigured)
        {
            return "unconfigured";
        }

        return assignment.Device is null
            ? "none"
            : $"device={assignment.Device.Value}";
    }

    private static string FormatNativeJoypads(IReadOnlyCollection<int> nativeJoypads)
    {
        if (nativeJoypads.Count == 0)
        {
            return "[]";
        }

        return $"[{string.Join(",", nativeJoypads.Select(device =>
            $"{device}:{FormatNativeJoypadValue(GetNativeJoypadProperty(device, "GetJoyName"))}:{FormatNativeJoypadValue(GetNativeJoypadProperty(device, "GetJoyGuid"))}"))}]";
    }

    private static string? GetNativeJoypadProperty(int device, string methodName)
    {
        try
        {
            var inputType = AccessTools.TypeByName("Godot.Input");
            return inputType?.GetMethod(methodName, Members, [typeof(int)])
                ?.Invoke(null, [device])
                ?.ToString();
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FormatNativeJoypadValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "<unknown>"
            : value.Replace(',', '_').Replace(':', '_').Replace(' ', '_');
    }

    private static string DescribeSteamControllerHandles(IReadOnlyList<object> handles)
    {
        if (handles.Count == 0)
        {
            return "[]";
        }

        var parts = new List<string>(handles.Count);
        for (var index = 0; index < handles.Count; index++)
        {
            var handle = handles[index];
            var inputType = GetInputTypeForHandle(handle)?.ToString() ?? "<unknown>";
            parts.Add($"{index}:{handle}:{inputType}");
        }

        return $"[{string.Join(",", parts)}]";
    }

    private static string DescribeSteamGamepadIndexHandles(
        object strategy,
        IReadOnlyList<object> connectedHandles,
        int? controllerClientCount)
    {
        var slotCount = Math.Clamp(Math.Max(controllerClientCount ?? 4, 4), 1, 16);
        var parts = new List<string>(slotCount);
        for (var index = 0; index < slotCount; index++)
        {
            if (!TryGetControllerHandleForGamepadIndex(strategy, index, out var mappedHandle) || mappedHandle is null)
            {
                return "<GetControllerForGamepadIndex unavailable>";
            }

            if (IsZeroSteamInputHandle(mappedHandle))
            {
                parts.Add($"{index}:0:<native-or-invalid>");
                continue;
            }

            var isConnected = TryFindConnectedControllerHandle(connectedHandles, mappedHandle, out var connectedHandle);
            var handle = connectedHandle ?? mappedHandle;
            var inputType = GetInputTypeForHandle(handle)?.ToString() ?? "<unknown>";
            parts.Add(isConnected
                ? $"{index}:{handle}:{inputType}"
                : $"{index}:{handle}:{inputType}:not-connected");
        }

        return $"[{string.Join(",", parts)}]";
    }

    private static bool IsZeroSteamInputHandle(object? handle)
    {
        if (handle is null)
        {
            return true;
        }

        var value = ReadHandleNumericValue(handle);
        if (value is not null)
        {
            return value.Value == 0;
        }

        var defaultValue = handle.GetType().IsValueType
            ? Activator.CreateInstance(handle.GetType())
            : null;
        return Equals(handle, defaultValue);
    }

    private static ulong? ReadHandleNumericValue(object handle)
    {
        foreach (var memberName in new[]
        {
            "Value",
            "m_InputHandle",
            "m_ControllerHandle",
            "m_InputActionSetHandle",
            "m_InputDigitalActionHandle",
            "m_Handle"
        })
        {
            var property = handle.GetType().GetProperty(memberName, Members);
            if (TryConvertUInt64(property?.GetValue(handle), out var propertyValue))
            {
                return propertyValue;
            }

            var field = handle.GetType().GetField(memberName, Members);
            if (TryConvertUInt64(field?.GetValue(handle), out var fieldValue))
            {
                return fieldValue;
            }
        }

        if (handle is IConvertible convertible)
        {
            try
            {
                return convertible.ToUInt64(null);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryConvertUInt64(object? value, out ulong result)
    {
        switch (value)
        {
            case ulong ulongValue:
                result = ulongValue;
                return true;
            case IConvertible convertible:
                try
                {
                    result = convertible.ToUInt64(null);
                    return true;
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
                {
                    result = default;
                    return false;
                }
            default:
                result = default;
                return false;
        }
    }

    private static bool IsUnknownSteamInputType(object? controllerType)
    {
        return string.Equals(controllerType?.ToString(), "k_ESteamInputType_Unknown", StringComparison.Ordinal);
    }

    private static object? GetCurrentControllerHandle(object strategy)
    {
        var field = strategy.GetType().GetField("_currentControllerHandle", Members);
        return field?.GetValue(strategy);
    }

    private static void SetCurrentControllerHandle(object strategy, object handle)
    {
        var field = strategy.GetType().GetField("_currentControllerHandle", Members)
            ?? throw new MissingMemberException(strategy.GetType().FullName, "_currentControllerHandle");
        field.SetValue(strategy, handle);
    }

    private static void ClearCurrentControllerHandle(object strategy)
    {
        var field = strategy.GetType().GetField("_currentControllerHandle", Members)
            ?? throw new MissingMemberException(strategy.GetType().FullName, "_currentControllerHandle");
        field.SetValue(strategy, null);
    }

    private static object? RefreshControllerConfig(object strategy, object handle)
    {
        var controllerType = GetInputTypeForHandle(handle);
        if (controllerType is not null)
        {
            strategy.GetType().GetMethod("UpdateControllerConfig", Members, [controllerType.GetType()])
                ?.Invoke(strategy, [controllerType]);
            strategy.GetType().GetMethod("UpdateInputMap", Members, Type.EmptyTypes)
                ?.Invoke(strategy, null);
        }

        var actionSet = ResolveControlsActionSet(strategy);
        if (actionSet is null)
        {
            return controllerType;
        }

        ActivateSteamInputActionSet(handle, actionSet);
        return controllerType;
    }

    private static SteamControllerBindingAvailability EvaluateSteamControllerBindings(
        object handle,
        object? actionSet)
    {
        if (actionSet is null || IsZeroSteamInputHandle(actionSet))
        {
            return SteamControllerBindingAvailability.Unknown("action set unavailable");
        }

        try
        {
            var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
                ?? throw new MissingMemberException("Steamworks.SteamInput");
            var getDeviceBindingRevision = steamInputType.GetMethods(Members)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "GetDeviceBindingRevision", StringComparison.Ordinal)
                    && method.GetParameters() is
                    [var handleParameter, var majorParameter, var minorParameter]
                    && handleParameter.ParameterType.IsAssignableFrom(handle.GetType())
                    && majorParameter.ParameterType.IsByRef
                    && minorParameter.ParameterType.IsByRef);
            var bindingRevisionSummary = string.Empty;
            bool? bindingRevisionLoaded = null;
            if (getDeviceBindingRevision is not null)
            {
                object?[] revisionArguments = [handle, 0, 0];
                bindingRevisionLoaded = getDeviceBindingRevision.Invoke(null, revisionArguments) is true;
                if (bindingRevisionLoaded is true)
                {
                    bindingRevisionSummary = $" bindingRevision={revisionArguments[1]}.{revisionArguments[2]}";
                }
            }

            var getDigitalActionHandle = steamInputType.GetMethod(
                "GetDigitalActionHandle",
                Members,
                [typeof(string)]);
            var getDigitalActionOrigins = steamInputType.GetMethods(Members)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "GetDigitalActionOrigins", StringComparison.Ordinal)
                    && method.GetParameters() is
                    [var handleParameter, var actionSetParameter, var actionParameter, var originsParameter]
                    && handleParameter.ParameterType.IsAssignableFrom(handle.GetType())
                    && actionSetParameter.ParameterType.IsAssignableFrom(actionSet.GetType())
                    && originsParameter.ParameterType.IsArray);
            if (getDigitalActionHandle is null || getDigitalActionOrigins is null)
            {
                return SteamControllerBindingAvailability.Unknown("action origin API unavailable");
            }

            var originType = getDigitalActionOrigins.GetParameters()[3].ParameterType.GetElementType();
            if (originType is null)
            {
                return SteamControllerBindingAvailability.Unknown("action origin type unavailable");
            }

            var validActionHandles = 0;
            foreach (var actionName in new[] { "Confirm", "Select", "Cancel", "Up", "Down" })
            {
                var actionHandle = getDigitalActionHandle.Invoke(null, [actionName]);
                if (actionHandle is null || IsZeroSteamInputHandle(actionHandle))
                {
                    continue;
                }

                validActionHandles++;
                var origins = Array.CreateInstance(originType, SteamInputMaxOrigins);
                var originCount = getDigitalActionOrigins.Invoke(
                    null,
                    [handle, actionSet, actionHandle, origins]) is int count
                    ? Math.Clamp(count, 0, origins.Length)
                    : 0;
                if (originCount > 0)
                {
                    return SteamControllerBindingAvailability.Usable(
                        $"bound action origins available action={actionName} count={originCount}{bindingRevisionSummary}");
                }
            }

            if (bindingRevisionLoaded is false)
            {
                return SteamControllerBindingAvailability.Pending(
                    $"Steam binding configuration is still loading; no origins across {validActionHandles} core actions");
            }

            return SteamControllerBindingAvailability.Unusable(
                validActionHandles == 0
                    ? "no valid Steam action handles"
                    : $"no bound action origins across {validActionHandles} core actions{bindingRevisionSummary}");
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            return SteamControllerBindingAvailability.Unknown(
                $"action binding probe failed: {exception.GetType().Name}");
        }
    }

    private static object? ResolveControlsActionSet(object strategy)
    {
        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        var actionSet = steamInputType.GetMethod("GetActionSetHandle", Members, [typeof(string)])
            ?.Invoke(null, ["Controls"]);
        if (actionSet is not null)
        {
            strategy.GetType().GetField("_currentActionSetHandle", Members)?.SetValue(strategy, actionSet);
        }

        return actionSet;
    }

    private static object? GetCurrentActionSetHandle(object strategy)
    {
        return strategy.GetType().GetField("_currentActionSetHandle", Members)?.GetValue(strategy);
    }

    private static void RunSteamInputFrame()
    {
        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        var runFrame = steamInputType.GetMethod("RunFrame", Members, [typeof(bool)])
            ?? steamInputType.GetMethod("RunFrame", Members, Type.EmptyTypes);
        runFrame?.Invoke(null, runFrame.GetParameters().Length == 0 ? null : [true]);
    }

    private static void ActivateSteamInputActionSet(object handle, object actionSet)
    {
        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        steamInputType.GetMethod("ActivateActionSet", Members, [handle.GetType(), actionSet.GetType()])
            ?.Invoke(null, [handle, actionSet]);
    }

    private static object? GetInputTypeForHandle(object handle)
    {
        var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput")
            ?? throw new MissingMemberException("Steamworks.SteamInput");
        return steamInputType.GetMethod("GetInputTypeForHandle", Members, [handle.GetType()])
            ?.Invoke(null, [handle]);
    }

    private static string RefreshNativeGeneratedActionMap(object strategy)
    {
        try
        {
            var controllerConfig = strategy.GetType().GetField("_controllerConfig", Members)?.GetValue(strategy);
            var map = CreateNativeGeneratedActionMap(controllerConfig);
            SetNativeGeneratedActionMap(map);
            var configName = controllerConfig?.GetType().Name ?? "<none>";
            return $"nativeBridge=config={configName} mappings={map.Count}";
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            ResetNativeGeneratedActionMap();
            return $"nativeBridge=fallback reason={exception.GetType().Name}";
        }
    }

    private static Dictionary<string, string> CreateNativeGeneratedActionMap(object? controllerConfig)
    {
        var nativeGeneratedActionMap = CreateFallbackNativeGeneratedActionMap();
        if (controllerConfig is null)
        {
            return nativeGeneratedActionMap;
        }

        var steamInputControllerMap = ReadStringDictionaryProperty(controllerConfig, "SteamInputControllerMap");
        var defaultControllerInputMap = ReadStringDictionaryProperty(controllerConfig, "DefaultControllerInputMap");
        if (steamInputControllerMap.Count == 0 || defaultControllerInputMap.Count == 0)
        {
            return nativeGeneratedActionMap;
        }

        var steamVirtualButtons = new HashSet<string>(steamInputControllerMap.Values, StringComparer.Ordinal);
        foreach (var (nativeAction, virtualButton) in defaultControllerInputMap)
        {
            if (steamVirtualButtons.Contains(virtualButton))
            {
                nativeGeneratedActionMap[virtualButton] = nativeAction;
            }
        }

        return nativeGeneratedActionMap;
    }

    private static Dictionary<string, string> ReadStringDictionaryProperty(object source, string propertyName)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.GetType().GetProperty(propertyName, Members)?.GetValue(source) is not IDictionary sourceDictionary)
        {
            return dictionary;
        }

        foreach (DictionaryEntry entry in sourceDictionary)
        {
            var key = entry.Key?.ToString();
            var value = entry.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                dictionary[key] = value;
            }
        }

        return dictionary;
    }

    private static void SetNativeGeneratedActionMap(IReadOnlyDictionary<string, string> nativeGeneratedActionMap)
    {
        lock (Lock)
        {
            NativeGeneratedActionMap.Clear();
            foreach (var (sourceAction, nativeAction) in nativeGeneratedActionMap)
            {
                NativeGeneratedActionMap[sourceAction] = nativeAction;
            }
        }
    }

    private static void ResetNativeGeneratedActionMap()
    {
        lock (Lock)
        {
            NativeGeneratedActionMap.Clear();
            foreach (var (sourceAction, nativeAction) in CreateFallbackNativeGeneratedActionMap())
            {
                NativeGeneratedActionMap[sourceAction] = nativeAction;
            }
        }
    }

    private static Dictionary<string, string> CreateFallbackNativeGeneratedActionMap()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FallbackTopPanelSourceAction] = FallbackTopPanelNativeAction,
            ["controller_left_shoulder"] = "mega_view_deck_and_tab_left",
            ["controller_left_bumper"] = "mega_view_deck_and_tab_left",
            ["controller_right_shoulder"] = "mega_view_exhaust_pile_and_tab_right",
            ["controller_right_bumper"] = "mega_view_exhaust_pile_and_tab_right",
            ["controller_left_trigger"] = "mega_view_draw_pile",
            ["controller_right_trigger"] = "mega_view_discard_pile",
            ["controller_ps4_touchpad"] = "mega_view_map",
            ["controller_select_button"] = "mega_view_map",
            ["ui_controller_touch_pad"] = "mega_view_map",
            ["controller_start"] = "mega_pause_and_back",
            ["controller_start_button"] = "mega_pause_and_back",
            ["controller_joystick_press"] = "mega_peek"
        };
    }

    private static void RegisterGeneratedInputEventsFromStrategy(object strategy)
    {
        var events = new List<object?>();
        var inputEvents = strategy.GetType().GetField("_inputEvents", Members)?.GetValue(strategy);
        if (inputEvents is IDictionary dictionary)
        {
            foreach (var value in dictionary.Values)
            {
                events.Add(value);
            }
        }

        events.Add(strategy.GetType().GetField("_joystickXAxis", Members)?.GetValue(strategy));
        events.Add(strategy.GetType().GetField("_joystickYAxis", Members)?.GetValue(strategy));
        RegisterGeneratedInputEvents(events);
    }

    private static void ClearPressedInputs(object strategy)
    {
        if (strategy.GetType().GetField("_pressedInputs", Members)?.GetValue(strategy) is IList pressedInputs)
        {
            pressedInputs.Clear();
        }
    }

    private static void ClearGeneratedInputEvents()
    {
        lock (Lock)
        {
            GeneratedInputEvents.Clear();
            AcceptedUiCompanionInputEvents.Clear();
            AcceptedOriginalSteamControllerInputEvents.Clear();
            AcceptedNativeGeneratedInputEvents.Clear();
            PendingUiCompanionActions.Clear();
            PendingOriginalSteamControllerInputs.Clear();
            PendingNativeGeneratedActions.Clear();
        }
    }

    private static object? GetKnownControllerHandle(BrokerControllerDeviceAssignment assignment)
    {
        if (!assignment.IsConfigured || assignment.Device is null)
        {
            return null;
        }

        lock (Lock)
        {
            return _knownControllerDevice == assignment.Device.Value
                ? _knownControllerHandle
                : null;
        }
    }

    private static void SetSelectedControllerDevice(int controllerDevice, object handle, bool rememberHandle = true)
    {
        lock (Lock)
        {
            _selectedControllerDevice = controllerDevice;
            if (rememberHandle)
            {
                _knownControllerDevice = controllerDevice;
                _knownControllerHandle = handle;
            }
        }
    }

    private static void ClearSelectedControllerDevice()
    {
        lock (Lock)
        {
            _selectedControllerDevice = null;
        }
    }

    private static void ClearSelectedControllerIdentity()
    {
        lock (Lock)
        {
            _selectedControllerDevice = null;
            _knownControllerDevice = null;
            _knownControllerHandle = null;
        }
    }

    private static void PruneExpiredUiCompanionTokens(
        string action,
        Queue<DateTimeOffset> pendingTokens,
        DateTimeOffset now)
    {
        while (pendingTokens.Count > 0 && now - pendingTokens.Peek() > UiCompanionTokenLifetime)
        {
            pendingTokens.Dequeue();
        }

        if (pendingTokens.Count == 0)
        {
            PendingUiCompanionActions.Remove(action);
        }
    }

    private static void PruneExpiredOriginalSteamControllerInputTokens(
        string key,
        Queue<DateTimeOffset> pendingTokens,
        DateTimeOffset now)
    {
        while (pendingTokens.Count > 0 && now - pendingTokens.Peek() > UiCompanionTokenLifetime)
        {
            pendingTokens.Dequeue();
        }

        if (pendingTokens.Count == 0)
        {
            PendingOriginalSteamControllerInputs.Remove(key);
        }
    }

    private static void PruneExpiredNativeGeneratedActionTokens(
        string action,
        Queue<DateTimeOffset> pendingTokens,
        DateTimeOffset now)
    {
        while (pendingTokens.Count > 0 && now - pendingTokens.Peek() > UiCompanionTokenLifetime)
        {
            pendingTokens.Dequeue();
        }

        if (pendingTokens.Count == 0)
        {
            PendingNativeGeneratedActions.Remove(action);
        }
    }

    private static string? GetOriginalSteamControllerInputKey(object? inputEvent)
    {
        if (inputEvent is null || !CanTrustOriginalSteamControllerInput(inputEvent))
        {
            return null;
        }

        var typeName = inputEvent.GetType().Name;
        var device = GetPropertyValue(inputEvent, "Device")?.ToString() ?? "<none>";
        var action = GetActionName(inputEvent);
        if (action is not null)
        {
            var pressed = GetPropertyValue(inputEvent, "Pressed")?.ToString() ?? "<none>";
            return $"{typeName}|device={device}|action={action}|pressed={pressed}";
        }

        var axis = GetPropertyValue(inputEvent, "Axis")?.ToString() ?? "<none>";
        return $"{typeName}|device={device}|axis={axis}";
    }

    private static bool IsNativeJoypadButton(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return false;
        }

        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        return typeName.Contains("JoypadButton", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedActionFromNativeFallback(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment)
    {
        if (inputEvent is null || assignment is not { IsConfigured: true, Device: not null })
        {
            return false;
        }

        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        if (!typeName.Contains("InputEventAction", StringComparison.OrdinalIgnoreCase)
            || GetPressedState(inputEvent) is null)
        {
            return false;
        }

        var action = GetActionName(inputEvent);
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        return !DeviceMatches(inputEvent, assignment);
    }

    private static bool DeviceMatches(object? inputEvent, BrokerControllerDeviceAssignment assignment)
    {
        return assignment is { IsConfigured: true, Device: not null }
            && GetPropertyValue(inputEvent, "Device") is int device
            && device == assignment.Device.Value;
    }

    private static object? GetPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return source.GetType().GetProperty(propertyName, Members)?.GetValue(source);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static object? TryDuplicateInputEvent(object inputEvent)
    {
        try
        {
            var duplicateWithSubresources = inputEvent.GetType()
                .GetMethod("Duplicate", Members, [typeof(bool)])
                ?.Invoke(inputEvent, [false]);
            if (duplicateWithSubresources is not null)
            {
                return duplicateWithSubresources;
            }

            var duplicate = inputEvent.GetType()
                .GetMethod("Duplicate", Members, Type.EmptyTypes)
                ?.Invoke(inputEvent, null);
            if (duplicate is not null)
            {
                return duplicate;
            }
        }
        catch (TargetInvocationException)
        {
            return null;
        }

        return null;
    }

    private static bool TrySetActionName(object inputEvent, string actionName)
    {
        var actionProperty = inputEvent.GetType().GetProperty("Action", Members);
        if (actionProperty is null || !actionProperty.CanWrite)
        {
            return false;
        }

        try
        {
            var actionValue = ConvertActionName(actionName, actionProperty.PropertyType);
            if (actionValue is null)
            {
                return false;
            }

            actionProperty.SetValue(inputEvent, actionValue);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static object? ConvertActionName(string actionName, Type targetType)
    {
        if (targetType == typeof(string) || targetType.IsAssignableFrom(typeof(string)))
        {
            return actionName;
        }

        var implicitConversion = targetType.GetMethod(
            "op_Implicit",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(string)]);
        if (implicitConversion is not null && implicitConversion.ReturnType == targetType)
        {
            return implicitConversion.Invoke(null, [actionName]);
        }

        var constructor = targetType.GetConstructor([typeof(string)]);
        if (constructor is not null)
        {
            return constructor.Invoke([actionName]);
        }

        return null;
    }

    public static string? GetActionName(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return null;
        }

        try
        {
            return inputEvent.GetType().GetProperty("Action", Members)?.GetValue(inputEvent)?.ToString();
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static bool IsPressedInput(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return false;
        }

        try
        {
            var pressedProperty = inputEvent.GetType().GetProperty("Pressed", Members);
            return pressedProperty is null || pressedProperty.GetValue(inputEvent) is not false;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    private static bool? GetPressedState(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return null;
        }

        try
        {
            return inputEvent.GetType().GetProperty("Pressed", Members)?.GetValue(inputEvent) is bool pressed
                ? pressed
                : null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static string? MapSteamControllerActionToUiCompanion(string? action)
    {
        return action switch
        {
            "controller_d_pad_west" => "ui_left",
            "controller_d_pad_east" => "ui_right",
            "controller_d_pad_north" => "ui_up",
            "controller_d_pad_south" => "ui_down",
            "controller_face_button_south" => "ui_select",
            "controller_face_button_east" => "ui_cancel",
            "controller_face_button_north" => "ui_accept",
            _ => null
        };
    }

    private static string? MapSteamControllerActionToNativeGeneratedAction(string? action)
    {
        if (action is null)
        {
            return null;
        }

        lock (Lock)
        {
            return NativeGeneratedActionMap.TryGetValue(action, out var nativeAction)
                ? nativeAction
                : null;
        }
    }

    private static void LogIfChanged(string message, Action<string> log)
    {
        lock (Lock)
        {
            if (message == _lastSelectionSummary)
            {
                return;
            }

            _lastSelectionSummary = message;
        }

        log(message);
    }
}

public sealed record SteamControllerHandleSelection<T>(
    bool Selected,
    int Index,
    T? Handle,
    string Reason,
    bool RememberHandle = true);

internal readonly record struct NativeFallbackGeneratedActionToken(DateTimeOffset Timestamp, bool Pressed);

internal readonly record struct SteamControllerBindingAvailability(
    bool IsKnown,
    bool IsUsable,
    bool IsPending,
    string Summary)
{
    public static SteamControllerBindingAvailability Unknown(string summary)
    {
        return new SteamControllerBindingAvailability(false, false, false, summary);
    }

    public static SteamControllerBindingAvailability Usable(string summary)
    {
        return new SteamControllerBindingAvailability(true, true, false, summary);
    }

    public static SteamControllerBindingAvailability Unusable(string summary)
    {
        return new SteamControllerBindingAvailability(true, false, false, summary);
    }

    public static SteamControllerBindingAvailability Pending(string summary)
    {
        return new SteamControllerBindingAvailability(false, false, true, summary);
    }
}

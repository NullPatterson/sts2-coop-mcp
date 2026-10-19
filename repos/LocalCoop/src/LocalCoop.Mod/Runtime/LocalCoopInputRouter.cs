using System.Reflection;
using HarmonyLib;

namespace LocalCoop.Mod.Runtime;

public static class LocalCoopInputRouter
{
    private static readonly object StrategyLock = new();
    private static WeakReference<object>? _steamInputStrategy;

    public static ClientControllerAssignment ResolveAssignment(BrokerClientConfig config)
    {
        var assignment = ControllerAssignmentService.Resolve(config);
        return new ClientControllerAssignment(
            config.ClientIndex,
            assignment.PlayerSlot,
            assignment.InputMode,
            assignment.InputMode == BrokerClientInputMode.None
                ? ControllerInputSource.None
                : ControllerInputSource.SteamInput,
            null,
            assignment.InputMode == BrokerClientInputMode.None
                ? "inputMode=none"
                : "SteamInput primary; native fallback if unavailable",
            assignment.ControllerDevice,
            assignment.ControllerClientCount,
            config.SessionId);
    }

    public static void ApplyControllerSelection(
        object strategy,
        ClientControllerAssignment assignment,
        Action<string> log)
    {
        RememberControllerInputStrategy(strategy);
        SteamControllerInputSelection.ApplySelection(
            strategy,
            assignment.ControllerDevice,
            assignment.ControllerClientCount,
            assignment.SessionId,
            assignment.ClientIndex,
            message => log(FormatRuntimeAssignmentLog(assignment, message)));
    }

    public static void RememberControllerInputStrategy(object strategy)
    {
        lock (StrategyLock)
        {
            _steamInputStrategy = new WeakReference<object>(strategy);
        }
    }

    public static bool TryGetRememberedControllerInputStrategy(out object? strategy)
    {
        lock (StrategyLock)
        {
            if (_steamInputStrategy is not null && _steamInputStrategy.TryGetTarget(out strategy))
            {
                return true;
            }

            strategy = null;
            return false;
        }
    }

    public static bool TryApplyControllerSelectionToRememberedStrategy(
        ClientControllerAssignment assignment,
        Action<string> log)
    {
        object? strategy;
        lock (StrategyLock)
        {
            if (_steamInputStrategy is null || !_steamInputStrategy.TryGetTarget(out strategy))
            {
                return false;
            }
        }

        ApplyControllerSelection(strategy, assignment, log);
        return true;
    }

    public static bool IsSelectedControllerActive(ClientControllerAssignment assignment)
    {
        return SteamControllerInputSelection.IsSelectedControllerActive(assignment.ControllerDevice);
    }

    public static BrokerControllerDeviceAssignment ResolveEffectiveControllerDevice(ClientControllerAssignment assignment)
    {
        return SteamControllerInputSelection.ResolveEffectiveControllerDevice(
            assignment.ControllerDevice,
            assignment.ControllerClientCount);
    }

    public static ControllerSourceObservation ObserveSelectedSteamSource(object? inputEvent)
    {
        var canonicalAction = TryMapCanonicalAction(inputEvent);
        if (canonicalAction is not null)
        {
            SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(inputEvent);
            SteamControllerInputSelection.RegisterGeneratedNativeAction(inputEvent);
        }

        return new ControllerSourceObservation(
            canonicalAction is not null,
            canonicalAction,
            SteamControllerInputSelection.GetMappedTargetAction(inputEvent));
    }

    public static void ObserveSelectedOriginalSteamSource(object? inputEvent)
    {
        SteamControllerInputSelection.RegisterGeneratedOriginalSteamControllerInput(inputEvent);
        SteamControllerInputSelection.RegisterGeneratedNativeAction(inputEvent);
    }

    public static void ObserveNativeFallbackSource(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment)
    {
        SteamControllerInputSelection.RegisterNativeFallbackSourceInput(inputEvent, assignment);
    }

    public static bool TryConsumeNativeFallbackGeneratedInput(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment)
    {
        return SteamControllerInputSelection.TryConsumeNativeFallbackGeneratedInput(inputEvent, assignment);
    }

    public static bool TryDeliverCanonicalInputToSink(
        object instance,
        MethodBase method,
        object? sourceInputEvent,
        out CanonicalInputDelivery delivery)
    {
        delivery = CanonicalInputDelivery.NotDelivered("unmapped");

        var canonicalAction = TryMapCanonicalAction(sourceInputEvent);
        if (canonicalAction is null)
        {
            return false;
        }

        if (!SteamControllerInputSelection.TryCreateMappedInputEvent(sourceInputEvent, out var mappedInputEvent, out var targetAction)
            || mappedInputEvent is null
            || string.IsNullOrWhiteSpace(targetAction))
        {
            delivery = CanonicalInputDelivery.NotDelivered($"canonical={canonicalAction} reason=create-failed");
            return false;
        }

        SteamControllerInputSelection.AcceptGeneratedMappedInputEvent(mappedInputEvent, targetAction);
        if (TryParseInputEvent(mappedInputEvent, out var parseFailure))
        {
            delivery = new CanonicalInputDelivery(
                Delivered: true,
                CanonicalAction: canonicalAction,
                TargetAction: targetAction,
                Method: "Godot.Input.ParseInputEvent",
                Reason: "parsed");
            return true;
        }

        if (!IsAuthoritativeDirectSink(instance.GetType().FullName ?? instance.GetType().Name, method.Name))
        {
            delivery = new CanonicalInputDelivery(
                Delivered: false,
                CanonicalAction: canonicalAction,
                TargetAction: targetAction,
                Method: $"{instance.GetType().Name}.{method.Name}",
                Reason: $"non-authoritative-sink; parse={parseFailure}; allowing original");
            return false;
        }

        try
        {
            method.Invoke(instance, [mappedInputEvent]);
            delivery = new CanonicalInputDelivery(
                Delivered: true,
                CanonicalAction: canonicalAction,
                TargetAction: targetAction,
                Method: $"{instance.GetType().Name}.{method.Name}",
                Reason: "direct-authoritative");
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            delivery = new CanonicalInputDelivery(
                Delivered: false,
                CanonicalAction: canonicalAction,
                TargetAction: targetAction,
                Method: $"{instance.GetType().Name}.{method.Name}",
                Reason: $"{exception.GetType().Name}; allowing original");
            return false;
        }
    }

    public static bool IsAuthoritativeDirectSink(string typeName, string methodName)
    {
        return (string.Equals(methodName, "_Input", StringComparison.Ordinal)
                && typeName.EndsWith("NCharacterSelectScreen", StringComparison.Ordinal))
            || (string.Equals(methodName, "_UnhandledInput", StringComparison.Ordinal)
                && typeName.EndsWith("NInputManager", StringComparison.Ordinal));
    }

    public static CanonicalInputAction? TryMapCanonicalAction(object? inputEvent)
    {
        return SteamControllerInputSelection.GetActionName(inputEvent) switch
        {
            "controller_d_pad_north" => CanonicalInputAction.Up,
            "controller_d_pad_south" => CanonicalInputAction.Down,
            "controller_d_pad_west" => CanonicalInputAction.Left,
            "controller_d_pad_east" => CanonicalInputAction.Right,
            "controller_face_button_south" => CanonicalInputAction.Select,
            "controller_face_button_east" => CanonicalInputAction.Cancel,
            "controller_face_button_west" => CanonicalInputAction.TopPanel,
            "controller_left_shoulder" or "controller_left_bumper" => CanonicalInputAction.TabLeft,
            "controller_right_shoulder" or "controller_right_bumper" => CanonicalInputAction.TabRight,
            "controller_left_trigger" => CanonicalInputAction.PileLeft,
            "controller_right_trigger" => CanonicalInputAction.PileRight,
            "controller_ps4_touchpad" or "controller_select_button" or "ui_controller_touch_pad" => CanonicalInputAction.Map,
            "controller_start" or "controller_start_button" => CanonicalInputAction.Settings,
            "controller_face_button_north" => CanonicalInputAction.Confirm,
            "controller_joystick_press" => CanonicalInputAction.Peek,
            _ => null
        };
    }

    private static string FormatRuntimeAssignmentLog(ClientControllerAssignment assignment, string message)
    {
        return "LocalCoop input router: "
            + $"clientIndex={assignment.ClientIndex} "
            + $"playerSlot={assignment.PlayerSlot} "
            + $"inputMode={assignment.InputMode.ToString().ToLowerInvariant()} "
            + $"controllerClientCount={assignment.ControllerClientCount?.ToString() ?? "<unknown>"} "
            + $"source={assignment.SelectedSource} "
            + $"fallback=\"{assignment.FallbackReason}\" "
            + message;
    }

    private static bool TryParseInputEvent(object inputEvent, out string failureReason)
    {
        failureReason = "<none>";

        try
        {
            var inputType = AccessTools.TypeByName("Godot.Input");
            var parseInputEvent = inputType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "ParseInputEvent", StringComparison.Ordinal)
                    && method.GetParameters() is [var parameter]
                    && parameter.ParameterType.IsAssignableFrom(inputEvent.GetType()));
            if (parseInputEvent is null)
            {
                failureReason = "ParseInputEvent unavailable";
                return false;
            }

            parseInputEvent.Invoke(null, [inputEvent]);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            failureReason = exception.GetType().Name;
            return false;
        }
    }
}

public sealed record ControllerIdentity(
    object? SteamHandle,
    object? SteamInputType,
    int? SteamGamepadIndex,
    int? XInputSlot,
    int? GodotJoyId,
    int? VendorId,
    int? ProductId,
    string? DisplayName);

public sealed record ClientControllerAssignment(
    int ClientIndex,
    int PlayerSlot,
    BrokerClientInputMode InputMode,
    ControllerInputSource SelectedSource,
    ControllerIdentity? Identity,
    string FallbackReason,
    BrokerControllerDeviceAssignment ControllerDevice,
    int? ControllerClientCount,
    string SessionId);

public enum ControllerInputSource
{
    None,
    SteamInput,
    XInput,
    Godot
}

public enum CanonicalInputAction
{
    Up,
    Down,
    Left,
    Right,
    Confirm,
    Cancel,
    Select,
    TopPanel,
    TabLeft,
    TabRight,
    PileLeft,
    PileRight,
    Map,
    Settings,
    Peek
}

public sealed record ControllerSourceObservation(
    bool Accepted,
    CanonicalInputAction? CanonicalAction,
    string? TargetAction);

public sealed record CanonicalInputDelivery(
    bool Delivered,
    CanonicalInputAction? CanonicalAction,
    string? TargetAction,
    string? Method,
    string Reason)
{
    public static CanonicalInputDelivery NotDelivered(string reason)
    {
        return new CanonicalInputDelivery(false, null, null, null, reason);
    }
}

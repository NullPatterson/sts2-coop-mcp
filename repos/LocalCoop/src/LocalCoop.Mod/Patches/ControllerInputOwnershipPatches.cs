using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class ControllerInputOwnershipPatches
{
    private static readonly (string TypeName, string[] MethodNames)[] Targets =
    [
        (
            "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager",
            ["_Input"]
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager",
            ["_Ready", "_UnhandledInput"]
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager",
            ["_UnhandledInput"]
        ),
        (
            "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen",
            ["_Input"]
        )
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (typeName, methodNames) in Targets)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type is null)
            {
                continue;
            }

            foreach (var methodName in methodNames)
            {
                var method = AccessTools.Method(type, methodName);
                if (method is not null)
                {
                    yield return method;
                }
            }
        }
    }

    public static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        var settings = LoadSettings();
        if (!settings.Enabled || settings.Config is null)
        {
            return true;
        }

        var clientAssignment = LocalCoopInputRouter.ResolveAssignment(settings.Config);
        var configuredControllerAssignment = clientAssignment.ControllerDevice;
        var inputEvent = __args.FirstOrDefault();
        if (DynamicControllerCoordinator.IsEnabled)
        {
            var instanceTypeName = __instance.GetType().FullName ?? __instance.GetType().Name;
            if (string.Equals(instanceTypeName, "MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager", StringComparison.Ordinal))
            {
                DynamicControllerInputBridge.RememberInputManager(__instance);
            }

            var isGeneratedInput = SteamControllerInputSelection.IsGeneratedInputEvent(inputEvent);
            if (isGeneratedInput
                && string.Equals(instanceTypeName, "MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager", StringComparison.Ordinal)
                && string.Equals(__originalMethod.Name, "_UnhandledInput", StringComparison.Ordinal))
            {
                var dispatched = DynamicControllerInputBridge.DispatchMappedActions(__instance, inputEvent!);
                if (dispatched > 0)
                {
                    new BrokerEventLog(settings.EventLogPath).Write(
                        $"Dynamic controller input mapped for background client: dispatched={dispatched}.");
                    MarkInputHandled(__instance, inputEvent);
                    return false;
                }
            }

            DynamicControllerCoordinator.ObservePhysicalInput(inputEvent);
            var allowed = DynamicControllerCoordinator.ShouldAllowControllerInput(inputEvent);
            if (allowed
                && string.Equals(instanceTypeName, "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager", StringComparison.Ordinal)
                && string.Equals(__originalMethod.Name, "_Input", StringComparison.Ordinal)
                && isGeneratedInput)
            {
                var action = inputEvent?.GetType()
                    .GetProperty("Action", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(inputEvent)?.ToString() ?? "<unknown>";
                var pressed = IsPressedInput(inputEvent);
                new BrokerEventLog(settings.EventLogPath).Write(
                    $"Dynamic generated input reached controller manager: action={action} pressed={pressed}.");
            }

            if (!allowed)
            {
                MarkInputHandled(__instance, inputEvent);
            }

            return allowed;
        }

        var typeName = __instance.GetType().FullName ?? __instance.GetType().Name;
        var methodName = __originalMethod.Name;
        var isGeneratedSteamInput = SteamControllerInputSelection.IsGeneratedInputEvent(inputEvent);
        var isSelectedControllerActive = LocalCoopInputRouter.IsSelectedControllerActive(clientAssignment);
        if (ShouldRetrySteamSelectionFromInputBoundary(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isSelectedControllerActive))
        {
            LocalCoopInputRouter.TryApplyControllerSelectionToRememberedStrategy(
                clientAssignment,
                message => new BrokerEventLog(settings.EventLogPath).Write(message));
            isSelectedControllerActive = LocalCoopInputRouter.IsSelectedControllerActive(clientAssignment);
        }

        var controllerAssignment = LocalCoopInputRouter.ResolveEffectiveControllerDevice(clientAssignment);
        var isSelectedSteamControllerBoundary = ShouldTrustSelectedSteamControllerBoundary(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isGeneratedSteamInput);
        if (isSelectedSteamControllerBoundary)
        {
            var observation = LocalCoopInputRouter.ObserveSelectedSteamSource(inputEvent);
            LogSelectedSteamControllerBoundary(
                settings,
                inputEvent,
                observation,
                __instance,
                __originalMethod);
        }

        var isSelectedOriginalSteamControllerBoundary = ShouldTrustSelectedOriginalSteamControllerBoundary(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isGeneratedSteamInput);
        if (isSelectedOriginalSteamControllerBoundary)
        {
            LocalCoopInputRouter.ObserveSelectedOriginalSteamSource(inputEvent);
            LogSelectedOriginalSteamControllerBoundary(
                settings,
                inputEvent,
                __instance,
                __originalMethod);
        }

        if (IsControllerManagerObserver(typeName, methodName))
        {
            return true;
        }

        if (ShouldSuppressNativeControllerInputForSelectedSteamController(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isSelectedControllerActive))
        {
            var nativeDuplicateResult = ControllerInputOwnership.ShouldProcess(inputEvent, controllerAssignment) with
            {
                ShouldProcess = false,
                Reason = "selected Steam controller active; native Godot controller input suppressed"
            };
            MarkInputHandled(__instance, inputEvent);
            LogSuppressedIfUseful(
                settings,
                inputEvent,
                nativeDuplicateResult,
                __instance,
                __originalMethod);
            return false;
        }

        var shouldBridgeSelectedSteamInput = ShouldBridgeSelectedSteamInputAtSink(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isGeneratedSteamInput);
        if (shouldBridgeSelectedSteamInput)
        {
            var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
                __instance,
                __originalMethod,
                inputEvent,
                out var delivery);
            var bridgedResult = ControllerInputOwnership.ShouldProcess(
                inputEvent,
                controllerAssignment,
                trustAsSelectedControllerInput: true) with
            {
                ShouldProcess = !delivered,
                Reason = delivered
                    ? "bridged selected Steam controller action to generated action"
                    : "canonical bridge failed; allowing selected Steam controller action"
            };
            new BrokerEventLog(settings.EventLogPath).Write(
                FormatControllerOwnershipLogLine(
                    bridgedResult,
                    inputEvent,
                    __instance.GetType().Name,
                    __originalMethod.Name,
                    FormatCanonicalDeliverySuffix(delivery)));
            if (delivered)
            {
                MarkInputHandled(__instance, inputEvent);
                return false;
            }

            return true;
        }

        var shouldAllowSelectedSteamInputThroughNonAuthoritativeSink = ShouldAllowSelectedSteamInputThroughNonAuthoritativeSink(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isGeneratedSteamInput);
        if (shouldAllowSelectedSteamInputThroughNonAuthoritativeSink)
        {
            var bypassedResult = ControllerInputOwnership.ShouldProcess(
                inputEvent,
                controllerAssignment,
                trustAsSelectedControllerInput: true) with
            {
                Reason = "selected Steam controller action bypassed non-authoritative bridge sink"
            };
            new BrokerEventLog(settings.EventLogPath).Write(
                FormatControllerOwnershipLogLine(
                    bypassedResult,
                    inputEvent,
                    __instance.GetType().Name,
                    __originalMethod.Name,
                    "canonicalDelivery=bypassed deliveryReason=\"non-authoritative-sink\""));
            return true;
        }

        var isGeneratedUiCompanionInput = ShouldConsumeGeneratedUiCompanionAtSink(typeName, methodName)
            && SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(inputEvent);
        var isGeneratedNativeInput = ShouldConsumeGeneratedNativeActionAtSink(typeName, methodName)
            && SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(inputEvent);
        var isGeneratedOriginalSteamInput = ShouldConsumeGeneratedOriginalSteamInputAtSink(typeName, methodName)
            && SteamControllerInputSelection.TryConsumeGeneratedOriginalSteamControllerInput(inputEvent);
        var isGeneratedNativeFallbackInput = ShouldConsumeGeneratedNativeActionAtSink(typeName, methodName)
            && LocalCoopInputRouter.TryConsumeNativeFallbackGeneratedInput(inputEvent, controllerAssignment);
        var isSelectedSteamInput = ShouldTrustSelectedSteamInputAtSink(
            typeName,
            methodName,
            inputEvent,
            configuredControllerAssignment,
            isGeneratedSteamInput || isGeneratedOriginalSteamInput);

        var result = ControllerInputOwnership.ShouldProcess(
            inputEvent,
            controllerAssignment,
            isGeneratedUiCompanionInput || isGeneratedNativeInput || isGeneratedNativeFallbackInput || isSelectedSteamInput);
        if (!result.IsControllerInput)
        {
            return true;
        }

        if (result.ShouldProcess)
        {
            LocalCoopInputRouter.ObserveNativeFallbackSource(inputEvent, controllerAssignment);
            LogAllowedIfUseful(
                settings,
                inputEvent,
                result,
                __instance,
                __originalMethod,
                isGeneratedOriginalSteamInput || isGeneratedNativeInput || isGeneratedNativeFallbackInput,
                FormatGeneratedInputSuffix(
                    isGeneratedOriginalSteamInput,
                    isGeneratedNativeInput,
                    isGeneratedNativeFallbackInput));
            return true;
        }

        MarkInputHandled(__instance, inputEvent);
        LogSuppressedIfUseful(
            settings,
            inputEvent,
            result,
            __instance,
            __originalMethod,
            suffix: $"generatedSteamInput={isGeneratedSteamInput} generatedOriginalSteamInput={isGeneratedOriginalSteamInput} generatedNativeInput={isGeneratedNativeInput} generatedNativeFallbackInput={isGeneratedNativeFallbackInput}");
        return false;
    }

    public static void Postfix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        if (!DynamicControllerCoordinator.IsEnabled)
        {
            return;
        }

        var inputEvent = __args.FirstOrDefault();
        if (inputEvent is null || !SteamControllerInputSelection.IsGeneratedInputEvent(inputEvent))
        {
            return;
        }

        var instanceTypeName = __instance.GetType().FullName ?? __instance.GetType().Name;
        var settings = LoadSettings();

        if (!string.Equals(instanceTypeName, "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager", StringComparison.Ordinal)
            || !string.Equals(__originalMethod.Name, "_Input", StringComparison.Ordinal))
        {
            return;
        }

        var dispatched = DynamicControllerInputBridge.DispatchMappedActions(inputEvent);
        if (dispatched == 0)
        {
            return;
        }

        if (settings.Enabled)
        {
            new BrokerEventLog(settings.EventLogPath).Write(
                $"Dynamic controller input mapped after controller manager: dispatched={dispatched}.");
        }

        MarkInputHandled(__instance, inputEvent);
    }

    public static string FormatControllerOwnershipLogLineForTesting(
        ControllerInputOwnershipResult result,
        object? inputEvent,
        string typeName,
        string methodName,
        string? suffix = null)
    {
        return FormatControllerOwnershipLogLine(result, inputEvent, typeName, methodName, suffix);
    }

    public static bool ShouldTrustSelectedSteamControllerBoundaryForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldTrustSelectedSteamControllerBoundary(typeName, methodName, inputEvent, assignment, selectedSteamInput);
    }

    public static bool ShouldTrustSelectedOriginalSteamControllerBoundaryForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldTrustSelectedOriginalSteamControllerBoundary(
            typeName,
            methodName,
            inputEvent,
            assignment,
            selectedSteamInput);
    }

    public static bool ShouldConsumeGeneratedUiCompanionAtSinkForTesting(
        string typeName,
        string methodName)
    {
        return ShouldConsumeGeneratedUiCompanionAtSink(typeName, methodName);
    }

    public static bool ShouldConsumeGeneratedNativeActionAtSinkForTesting(
        string typeName,
        string methodName)
    {
        return ShouldConsumeGeneratedNativeActionAtSink(typeName, methodName);
    }

    public static bool ShouldTrustSelectedSteamInputAtSinkForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldTrustSelectedSteamInputAtSink(typeName, methodName, inputEvent, assignment, selectedSteamInput);
    }

    public static bool ShouldBridgeSelectedSteamInputAtSinkForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldBridgeSelectedSteamInputAtSink(typeName, methodName, inputEvent, assignment, selectedSteamInput);
    }

    public static bool ShouldAllowSelectedSteamInputThroughNonAuthoritativeSinkForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldAllowSelectedSteamInputThroughNonAuthoritativeSink(
            typeName,
            methodName,
            inputEvent,
            assignment,
            selectedSteamInput);
    }

    public static bool ShouldSuppressGeneratedSteamInputForNativeControllerDeviceZeroForTesting(
        string typeName,
        string methodName,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return ShouldSuppressGeneratedSteamInputForNativeControllerDeviceZero(
            typeName,
            methodName,
            assignment,
            selectedSteamInput);
    }

    public static bool ShouldSuppressNativeControllerInputForSelectedSteamControllerForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedControllerActive)
    {
        return ShouldSuppressNativeControllerInputForSelectedSteamController(
            typeName,
            methodName,
            inputEvent,
            assignment,
            selectedControllerActive);
    }

    public static bool ShouldRetrySteamSelectionFromInputBoundaryForTesting(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedControllerActive)
    {
        return ShouldRetrySteamSelectionFromInputBoundary(
            typeName,
            methodName,
            inputEvent,
            assignment,
            selectedControllerActive);
    }

    public static bool ShouldLogSuppressedControllerInputForTesting(
        object? inputEvent,
        bool includeUnpressed = false)
    {
        return ShouldLogControllerInput(inputEvent, includeUnpressed);
    }

    private static bool ShouldTrustSelectedSteamControllerBoundary(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return selectedSteamInput
            && IsAssignedSelectedSteamDevice(assignment)
            && IsControllerManagerObserver(typeName, methodName)
            && (SteamControllerInputSelection.CanMapUiCompanionAction(inputEvent)
                || SteamControllerInputSelection.CanMapNativeGeneratedAction(inputEvent));
    }

    private static bool ShouldTrustSelectedOriginalSteamControllerBoundary(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return selectedSteamInput
            && IsAssignedSelectedSteamDevice(assignment)
            && IsControllerManagerObserver(typeName, methodName)
            && SteamControllerInputSelection.CanTrustOriginalSteamControllerInput(inputEvent);
    }

    private static bool ShouldRetrySteamSelectionFromInputBoundary(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedControllerActive)
    {
        return !selectedControllerActive
            && IsAssignedSelectedSteamDevice(assignment)
            && IsRealInputSink(typeName, methodName)
            && IsNativeJoypadInput(inputEvent);
    }

    private static bool ShouldConsumeGeneratedUiCompanionAtSink(
        string typeName,
        string methodName)
    {
        return (string.Equals(methodName, "_Input", StringComparison.Ordinal)
                && typeName.EndsWith(".NCharacterSelectScreen", StringComparison.Ordinal))
            || (string.Equals(methodName, "_UnhandledInput", StringComparison.Ordinal)
                && (typeName.EndsWith(".NHotkeyManager", StringComparison.Ordinal)
                    || typeName.EndsWith(".NInputManager", StringComparison.Ordinal)));
    }

    private static bool ShouldConsumeGeneratedOriginalSteamInputAtSink(
        string typeName,
        string methodName)
    {
        return IsRealInputSink(typeName, methodName);
    }

    private static bool ShouldConsumeGeneratedNativeActionAtSink(
        string typeName,
        string methodName)
    {
        return IsRealInputSink(typeName, methodName);
    }

    private static bool ShouldTrustSelectedSteamInputAtSink(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return selectedSteamInput
            && IsAssignedSelectedSteamDevice(assignment)
            && IsRealInputSink(typeName, methodName)
            && SteamControllerInputSelection.CanTrustOriginalSteamControllerInput(inputEvent);
    }

    private static bool ShouldBridgeSelectedSteamInputAtSink(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return selectedSteamInput
            && IsAssignedSelectedSteamDevice(assignment)
            && IsAuthoritativeBridgeSink(typeName, methodName)
            && (SteamControllerInputSelection.CanMapUiCompanionAction(inputEvent)
                || SteamControllerInputSelection.CanMapNativeGeneratedAction(inputEvent));
    }

    private static bool ShouldAllowSelectedSteamInputThroughNonAuthoritativeSink(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return selectedSteamInput
            && IsAssignedSelectedSteamDevice(assignment)
            && IsNonAuthoritativeBridgeSink(typeName, methodName)
            && (SteamControllerInputSelection.CanMapUiCompanionAction(inputEvent)
                || SteamControllerInputSelection.CanMapNativeGeneratedAction(inputEvent));
    }

    private static bool ShouldSuppressGeneratedSteamInputForNativeControllerDeviceZero(
        string typeName,
        string methodName,
        BrokerControllerDeviceAssignment assignment,
        bool selectedSteamInput)
    {
        return false;
    }

    private static bool ShouldSuppressNativeControllerInputForSelectedSteamController(
        string typeName,
        string methodName,
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool selectedControllerActive)
    {
        return selectedControllerActive
            && IsAssignedSelectedSteamDevice(assignment)
            && IsRealInputSink(typeName, methodName)
            && IsNativeJoypadInput(inputEvent);
    }

    private static bool IsNativeJoypadInput(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return false;
        }

        var inputTypeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        return inputTypeName.Contains("Joypad", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControllerManagerObserver(
        string typeName,
        string methodName)
    {
        return string.Equals(methodName, "_Input", StringComparison.Ordinal)
            && typeName.EndsWith(".NControllerManager", StringComparison.Ordinal);
    }

    private static bool IsCharacterSelectInputSink(
        string typeName,
        string methodName)
    {
        return string.Equals(methodName, "_Input", StringComparison.Ordinal)
            && typeName.EndsWith(".NCharacterSelectScreen", StringComparison.Ordinal);
    }

    private static bool IsGlobalMenuSink(
        string typeName,
        string methodName)
    {
        return string.Equals(methodName, "_UnhandledInput", StringComparison.Ordinal)
            && (typeName.EndsWith(".NHotkeyManager", StringComparison.Ordinal)
                || typeName.EndsWith(".NInputManager", StringComparison.Ordinal));
    }

    private static bool IsRealInputSink(
        string typeName,
        string methodName)
    {
        return IsCharacterSelectInputSink(typeName, methodName)
            || IsGlobalMenuSink(typeName, methodName);
    }

    private static bool IsAuthoritativeBridgeSink(
        string typeName,
        string methodName)
    {
        return IsCharacterSelectInputSink(typeName, methodName)
            || (string.Equals(methodName, "_UnhandledInput", StringComparison.Ordinal)
                && typeName.EndsWith(".NInputManager", StringComparison.Ordinal));
    }

    private static bool IsNonAuthoritativeBridgeSink(
        string typeName,
        string methodName)
    {
        return string.Equals(methodName, "_UnhandledInput", StringComparison.Ordinal)
            && typeName.EndsWith(".NHotkeyManager", StringComparison.Ordinal);
    }

    private static bool IsAssignedSelectedSteamDevice(BrokerControllerDeviceAssignment assignment)
    {
        return assignment.IsConfigured
            && assignment.Device is not null;
    }

    private static void LogSelectedSteamControllerBoundary(
        BrokerModeSettings settings,
        object? inputEvent,
        ControllerSourceObservation observation,
        object instance,
        MethodBase method)
    {
        var result = ControllerInputOwnership.ShouldProcess(
            inputEvent,
            LocalCoopInputRouter.ResolveAssignment(settings.Config!).ControllerDevice,
            trustAsSelectedControllerInput: true);
        new BrokerEventLog(settings.EventLogPath).Write(
            FormatControllerOwnershipLogLine(
                result,
                inputEvent,
                instance.GetType().Name,
                method.Name,
                $"boundary=selectedSteamController canonicalAccepted={observation.Accepted} canonicalAction={observation.CanonicalAction?.ToString() ?? "<none>"} targetAction={observation.TargetAction ?? "<none>"}"));
    }

    private static void LogSelectedOriginalSteamControllerBoundary(
        BrokerModeSettings settings,
        object? inputEvent,
        object instance,
        MethodBase method)
    {
        var result = ControllerInputOwnership.ShouldProcess(
            inputEvent,
            LocalCoopInputRouter.ResolveAssignment(settings.Config!).ControllerDevice,
            trustAsSelectedControllerInput: true);
        new BrokerEventLog(settings.EventLogPath).Write(
            FormatControllerOwnershipLogLine(
                result,
                inputEvent,
                instance.GetType().Name,
                method.Name,
                "boundary=selectedOriginalSteamController"));
    }

    private static void LogSuppressedIfUseful(
        BrokerModeSettings settings,
        object? inputEvent,
        ControllerInputOwnershipResult result,
        object instance,
        MethodBase method,
        bool includeUnpressed = false,
        string? suffix = null)
    {
        if (!ShouldLogControllerInput(inputEvent, includeUnpressed))
        {
            return;
        }

        new BrokerEventLog(settings.EventLogPath).Write(
            FormatControllerOwnershipLogLine(
                result,
                inputEvent,
                instance.GetType().Name,
                method.Name,
                suffix));
    }

    private static void LogAllowedIfUseful(
        BrokerModeSettings settings,
        object? inputEvent,
        ControllerInputOwnershipResult result,
        object instance,
        MethodBase method,
        bool includeUnpressed = false,
        string? suffix = null)
    {
        if (!ShouldLogControllerInput(inputEvent, includeUnpressed))
        {
            return;
        }

        new BrokerEventLog(settings.EventLogPath).Write(
            FormatControllerOwnershipLogLine(
                result,
                inputEvent,
                instance.GetType().Name,
                method.Name,
                suffix));
    }

    private static bool ShouldLogControllerInput(object? inputEvent, bool includeUnpressed)
    {
        return includeUnpressed
            ? inputEvent is not null
            : IsPressedInput(inputEvent);
    }

    private static string? FormatGeneratedInputSuffix(
        bool isGeneratedOriginalSteamInput,
        bool isGeneratedNativeInput,
        bool isGeneratedNativeFallbackInput)
    {
        if (!isGeneratedOriginalSteamInput && !isGeneratedNativeInput && !isGeneratedNativeFallbackInput)
        {
            return null;
        }

        return $"generatedOriginalSteamInput={isGeneratedOriginalSteamInput} generatedNativeInput={isGeneratedNativeInput} generatedNativeFallbackInput={isGeneratedNativeFallbackInput}";
    }

    private static string FormatCanonicalDeliverySuffix(CanonicalInputDelivery delivery)
    {
        return "canonicalDelivery="
            + FormatCanonicalDeliveryStatus(delivery)
            + $" canonicalAction={delivery.CanonicalAction?.ToString() ?? "<none>"}"
            + $" targetAction={delivery.TargetAction ?? "<none>"}"
            + $" deliveryMethod={delivery.Method ?? "<none>"}"
            + $" deliveryReason=\"{delivery.Reason}\"";
    }

    private static string FormatCanonicalDeliveryStatus(CanonicalInputDelivery delivery)
    {
        if (!delivery.Delivered)
        {
            return "failed";
        }

        return delivery.Reason switch
        {
            "parsed" => "parsed",
            "direct-authoritative" => "direct-authoritative",
            _ => "delivered"
        };
    }

    private static string FormatControllerOwnershipLogLine(
        ControllerInputOwnershipResult result,
        object? inputEvent,
        string typeName,
        string methodName,
        string? suffix = null)
    {
        var suffixText = string.IsNullOrWhiteSpace(suffix) ? string.Empty : $" {suffix}";
        var inputType = inputEvent?.GetType().Name ?? "<none>";
        return $"{ControllerInputOwnership.FormatLogLine(result, inputEvent)} inputType={inputType} method={typeName}.{methodName}{suffixText}";
    }

    private static bool IsPressedInput(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return false;
        }

        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        if (!typeName.Contains("InputEventAction", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("Button", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var property = inputEvent.GetType().GetProperty(
            "Pressed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(inputEvent) is true;
    }

    private static void MarkInputHandled(object instance, object? inputEvent)
    {
        TryInvoke(inputEvent, "SetAsHandled");
        var viewport = TryInvoke(instance, "GetViewport");
        TryInvoke(viewport, "SetInputAsHandled");
    }

    private static object? TryInvoke(object? source, string methodName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return source.GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
                ?.Invoke(source, null);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}

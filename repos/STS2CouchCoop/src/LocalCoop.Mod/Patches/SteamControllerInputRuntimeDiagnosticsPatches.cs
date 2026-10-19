using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class SteamControllerInputRuntimeDiagnosticsPatches
{
    private const string DebugTag = "Controller diagnostics:";
    private static readonly BindingFlags Members =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object Lock = new();
    private static readonly HashSet<string> LoggedFirstCalls = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedBindingSummaries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastActivityByMethod = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, XInputSlotSnapshot> LastXInputSnapshots = new();
    private static bool _loggedFirstXInputPoll;
    private static bool _loggedXInputUnavailable;

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.ControllerInput.SteamControllerInputStrategy");
        if (type is null)
        {
            yield break;
        }

        foreach (var method in type.GetMethods(Members | BindingFlags.DeclaredOnly))
        {
            if (method.IsAbstract
                || method.IsGenericMethod
                || method.IsSpecialName
                || string.Equals(method.Name, "UpdateControllerConnections", StringComparison.Ordinal)
                || !LooksLikeInputRuntimeMethod(method.Name))
            {
                continue;
            }

            yield return method;
        }
    }

    public static void Postfix(MethodBase __originalMethod, object __instance)
    {
        var settings = LoadSettings();
        if (!settings.Enabled || settings.Config is null)
        {
            return;
        }

        if (string.Equals(__originalMethod.Name, "ProcessInput", StringComparison.Ordinal))
        {
            PollXInputDiagnostics(settings);
        }

        var methodKey = __originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name;
        var pressedInputs = FormatPressedInputs(__instance);
        var pressedEvents = FormatPressedInputEvents(__instance);
        var currentHandle = FormatValue(GetFieldValue(__instance, "_currentControllerHandle"));
        var currentActionSet = FormatValue(GetFieldValue(__instance, "_currentActionSetHandle"));
        var summary = $"method={methodKey} handle={currentHandle} actionSet={currentActionSet} pressedInputs={pressedInputs} pressedEvents={pressedEvents}";
        var hasActivity = !string.Equals(pressedInputs, "[]", StringComparison.Ordinal)
            || !string.Equals(pressedEvents, "[]", StringComparison.Ordinal);

        lock (Lock)
        {
            if (!LoggedFirstCalls.Add(methodKey ?? "<unknown>")
                && (!hasActivity
                    || (LastActivityByMethod.TryGetValue(methodKey ?? "<unknown>", out var last)
                        && string.Equals(last, summary, StringComparison.Ordinal))))
            {
                return;
            }

            if (hasActivity)
            {
                LastActivityByMethod[methodKey ?? "<unknown>"] = summary;
            }
        }

        new BrokerEventLog(settings.EventLogPath).Write($"{DebugTag} Steam input runtime: {summary}");
        LogSteamInputBindingSummaryIfNeeded(settings, __instance, currentHandle, currentActionSet);
    }

    private static void LogSteamInputBindingSummaryIfNeeded(
        BrokerModeSettings settings,
        object strategy,
        string currentHandle,
        string currentActionSet)
    {
        if (string.Equals(currentHandle, "<null>", StringComparison.Ordinal)
            || string.Equals(currentActionSet, "<null>", StringComparison.Ordinal))
        {
            return;
        }

        var key = $"{currentHandle}|{currentActionSet}";
        lock (Lock)
        {
            if (!LoggedBindingSummaries.Add(key))
            {
                return;
            }
        }

        new BrokerEventLog(settings.EventLogPath).Write(
            $"{DebugTag} Steam input bindings: handle={currentHandle} actionSet={currentActionSet} "
            + FormatSteamInputBindings(strategy));
    }

    private static string FormatSteamInputBindings(object strategy)
    {
        try
        {
            var handle = GetFieldValue(strategy, "_currentControllerHandle");
            var actionSet = GetFieldValue(strategy, "_currentActionSetHandle");
            if (handle is null || actionSet is null)
            {
                return "unavailable reason=missing selected handle/action set";
            }

            var steamInputType = AccessTools.TypeByName("Steamworks.SteamInput");
            if (steamInputType is null)
            {
                return "unavailable reason=missing Steamworks.SteamInput";
            }

            var actions = new[]
            {
                "Confirm",
                "Select",
                "Cancel",
                "Up",
                "Down",
                "Left",
                "Right",
                "Top_Panel",
                "View_Draw_Pile",
                "View_Discard_Pile",
                "Tab_Left",
                "Tab_Right",
                "View_Map",
                "Settings",
                "Peek"
            };
            return $"inputEvents={FormatInputEventMap(strategy)} "
                + $"actions=[{string.Join(";", actions.Select(action => FormatDigitalActionOrigins(steamInputType, handle, actionSet, action)))}]";
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            return $"unavailable reason={FormatException(exception)} inputEvents={FormatInputEventMap(strategy)}";
        }
    }

    private static string FormatDigitalActionOrigins(Type steamInputType, object handle, object actionSet, string actionName)
    {
        try
        {
            var actionHandle = steamInputType.GetMethod("GetDigitalActionHandle", Members, [typeof(string)])
                ?.Invoke(null, [actionName]);
            if (actionHandle is null || IsZeroHandle(actionHandle))
            {
                return $"{actionName}=<no-action-handle>";
            }

            var originsMethod = steamInputType.GetMethods(Members)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "GetDigitalActionOrigins", StringComparison.Ordinal)
                    && method.GetParameters() is [var handleParameter, var actionSetParameter, var actionParameter, var originsParameter]
                    && handleParameter.ParameterType.IsAssignableFrom(handle.GetType())
                    && actionSetParameter.ParameterType.IsAssignableFrom(actionSet.GetType())
                    && actionParameter.ParameterType.IsAssignableFrom(actionHandle.GetType())
                    && originsParameter.ParameterType.IsArray);
            if (originsMethod is null)
            {
                return $"{actionName}=handle:{actionHandle}:<missing-origins-api>";
            }

            var originArrayType = originsMethod.GetParameters()[3].ParameterType.GetElementType();
            if (originArrayType is null)
            {
                return $"{actionName}=handle:{actionHandle}:<invalid-origins-api>";
            }

            var origins = Array.CreateInstance(originArrayType, SteamInputMaxOrigins);
            var count = originsMethod.Invoke(null, [handle, actionSet, actionHandle, origins]) is int originCount
                ? Math.Clamp(originCount, 0, origins.Length)
                : 0;
            if (count == 0)
            {
                return $"{actionName}=handle:{actionHandle}:<no-origins>";
            }

            var originNames = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                originNames.Add(FormatValue(origins.GetValue(index)));
            }

            return $"{actionName}=handle:{actionHandle}:origins:{string.Join("|", originNames)}";
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMemberException or InvalidOperationException or ArgumentException)
        {
            return $"{actionName}=<error:{FormatException(exception)}>";
        }
    }

    private static bool IsZeroHandle(object handle)
    {
        var value = handle.GetType().GetField("m_InputDigitalActionHandle", Members)?.GetValue(handle)
            ?? handle.GetType().GetField("m_InputActionSetHandle", Members)?.GetValue(handle)
            ?? handle.GetType().GetField("m_InputHandle", Members)?.GetValue(handle)
            ?? handle.GetType().GetField("Value", Members)?.GetValue(handle);
        return value is IConvertible convertible && convertible.ToUInt64(null) == 0;
    }

    private static string FormatInputEventMap(object strategy)
    {
        if (GetFieldValue(strategy, "_inputEvents") is not IDictionary inputEvents)
        {
            return "<missing>";
        }

        var parts = new List<string>();
        foreach (DictionaryEntry entry in inputEvents)
        {
            parts.Add($"{entry.Key}:{FormatInputEvent(entry.Value)}");
        }

        return FormatEnumerable(parts);
    }

    private static string FormatException(Exception exception)
    {
        var leaf = exception;
        while (leaf is TargetInvocationException { InnerException: not null } targetInvocationException)
        {
            leaf = targetInvocationException.InnerException!;
        }

        return $"{leaf.GetType().Name}: {leaf.Message}";
    }

    private static void PollXInputDiagnostics(BrokerModeSettings settings)
    {
        XInputSlotSnapshot[] snapshots;
        try
        {
            snapshots = Enumerable.Range(0, 4)
                .Select(ReadXInputSlot)
                .ToArray();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            lock (Lock)
            {
                if (_loggedXInputUnavailable)
                {
                    return;
                }

                _loggedXInputUnavailable = true;
            }

            new BrokerEventLog(settings.EventLogPath).Write(
                $"{DebugTag} XInput poll unavailable: {exception.GetType().Name}: {exception.Message}");
            return;
        }

        var changedRelevantButtons = new List<XInputSlotSnapshot>();
        lock (Lock)
        {
            foreach (var snapshot in snapshots)
            {
                if (!LastXInputSnapshots.TryGetValue(snapshot.Slot, out var previous)
                    || previous.RelevantButtons != snapshot.RelevantButtons
                    || previous.Connected != snapshot.Connected)
                {
                    if (_loggedFirstXInputPoll || snapshot.RelevantButtons != 0 || previous.Connected != snapshot.Connected)
                    {
                        changedRelevantButtons.Add(snapshot);
                    }

                    LastXInputSnapshots[snapshot.Slot] = snapshot;
                }
            }

            if (!_loggedFirstXInputPoll)
            {
                _loggedFirstXInputPoll = true;
                changedRelevantButtons.Clear();
                changedRelevantButtons.AddRange(snapshots);
            }
        }

        if (changedRelevantButtons.Count == 0)
        {
            return;
        }

        var config = settings.Config;
        if (config is null)
        {
            return;
        }

        new BrokerEventLog(settings.EventLogPath).Write(
            $"{DebugTag} XInput poll: clientIndex={config.ClientIndex} playerSlot={config.PlayerSlot} "
            + $"controllerDevice={config.ControllerDevice.Device?.ToString() ?? "none"} "
            + $"changed=[{string.Join(",", changedRelevantButtons.Select(FormatXInputSnapshot))}] "
            + $"all=[{string.Join(",", snapshots.Select(FormatXInputSnapshot))}]");
    }

    private static XInputSlotSnapshot ReadXInputSlot(int slot)
    {
        var result = XInputGetState(slot, out var state);
        return result == 0
            ? new XInputSlotSnapshot(
                slot,
                true,
                state.PacketNumber,
                state.Gamepad.Buttons,
                (ushort)(state.Gamepad.Buttons & XInputRelevantButtons))
            : new XInputSlotSnapshot(slot, false, 0, 0, 0);
    }

    private static string FormatXInputSnapshot(XInputSlotSnapshot snapshot)
    {
        if (!snapshot.Connected)
        {
            return $"slot={snapshot.Slot}:disconnected";
        }

        return $"slot={snapshot.Slot}:packet={snapshot.PacketNumber}:buttons={FormatXInputButtons(snapshot.RelevantButtons)}";
    }

    private static string FormatXInputButtons(ushort buttons)
    {
        if (buttons == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        if ((buttons & XInputButtonDpadUp) != 0)
        {
            parts.Add("Up");
        }

        if ((buttons & XInputButtonDpadDown) != 0)
        {
            parts.Add("Down");
        }

        if ((buttons & XInputButtonA) != 0)
        {
            parts.Add("A");
        }

        if ((buttons & XInputButtonX) != 0)
        {
            parts.Add("X");
        }

        return string.Join("|", parts);
    }

    private static bool LooksLikeInputRuntimeMethod(string methodName)
    {
        return methodName.Contains("Input", StringComparison.OrdinalIgnoreCase)
            || methodName.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || methodName.Contains("Process", StringComparison.OrdinalIgnoreCase)
            || methodName.Contains("Poll", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPressedInputs(object strategy)
    {
        return GetFieldValue(strategy, "_pressedInputs") is IList pressedInputs
            ? FormatEnumerable(pressedInputs.Cast<object?>().Select(FormatValue))
            : "<missing>";
    }

    private static string FormatPressedInputEvents(object strategy)
    {
        if (GetFieldValue(strategy, "_inputEvents") is not IDictionary inputEvents)
        {
            return "<missing>";
        }

        var parts = new List<string>();
        foreach (DictionaryEntry entry in inputEvents)
        {
            var inputEvent = entry.Value;
            if (GetPropertyValue(inputEvent, "Pressed") is not true)
            {
                continue;
            }

            parts.Add($"{entry.Key}:{FormatInputEvent(inputEvent)}");
        }

        return FormatEnumerable(parts);
    }

    private static string FormatInputEvent(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return "<null>";
        }

        var parts = new List<string>
        {
            inputEvent.GetType().Name
        };
        AppendProperty(parts, inputEvent, "Action", "action");
        AppendProperty(parts, inputEvent, "Device", "device");
        AppendProperty(parts, inputEvent, "ButtonIndex", "button");
        AppendProperty(parts, inputEvent, "Axis", "axis");
        AppendProperty(parts, inputEvent, "AxisValue", "axisValue");
        AppendProperty(parts, inputEvent, "Pressed", "pressed");
        return string.Join("|", parts);
    }

    private static void AppendProperty(List<string> parts, object source, string propertyName, string label)
    {
        var value = GetPropertyValue(source, propertyName);
        if (value is not null)
        {
            parts.Add($"{label}={value}");
        }
    }

    private static string FormatEnumerable(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? "[]"
            : $"[{string.Join(",", materialized)}]";
    }

    private static string FormatValue(object? value)
    {
        return value?.ToString() ?? "<null>";
    }

    private static object? GetFieldValue(object source, string fieldName)
    {
        try
        {
            return source.GetType().GetField(fieldName, Members)?.GetValue(source);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
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

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }

    private const ushort XInputButtonDpadUp = 0x0001;
    private const ushort XInputButtonDpadDown = 0x0002;
    private const ushort XInputButtonA = 0x1000;
    private const ushort XInputButtonX = 0x4000;
    private const ushort XInputRelevantButtons =
        XInputButtonDpadUp | XInputButtonDpadDown | XInputButtonA | XInputButtonX;
    private const int SteamInputMaxOrigins = 8;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct XInputState
    {
        public readonly uint PacketNumber;
        public readonly XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct XInputGamepad
    {
        public readonly ushort Buttons;
        public readonly byte LeftTrigger;
        public readonly byte RightTrigger;
        public readonly short ThumbLX;
        public readonly short ThumbLY;
        public readonly short ThumbRX;
        public readonly short ThumbRY;
    }

    private readonly record struct XInputSlotSnapshot(
        int Slot,
        bool Connected,
        uint PacketNumber,
        ushort Buttons,
        ushort RelevantButtons);
}

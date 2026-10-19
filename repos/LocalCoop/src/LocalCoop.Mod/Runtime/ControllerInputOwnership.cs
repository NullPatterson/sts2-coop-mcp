using System.Reflection;

namespace LocalCoop.Mod.Runtime;

public static class ControllerInputOwnership
{
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static ControllerInputOwnershipResult ShouldProcess(
        object? inputEvent,
        BrokerControllerDeviceAssignment assignment,
        bool trustAsSelectedControllerInput = false)
    {
        if (!assignment.IsConfigured)
        {
            return new ControllerInputOwnershipResult(
                ShouldProcess: true,
                IsControllerInput: false,
                Device: null,
                Reason: "controllerDevice is unconfigured");
        }

        if (inputEvent is null || !TryGetControllerDevice(inputEvent, out var device))
        {
            return new ControllerInputOwnershipResult(
                ShouldProcess: true,
                IsControllerInput: false,
                Device: null,
                Reason: "non-controller input");
        }

        if (assignment.Device is null)
        {
            return new ControllerInputOwnershipResult(
                ShouldProcess: false,
                IsControllerInput: true,
                Device: device,
                Reason: "controllerDevice=none");
        }

        if (trustAsSelectedControllerInput)
        {
            return new ControllerInputOwnershipResult(
                ShouldProcess: true,
                IsControllerInput: true,
                Device: device,
                Reason: $"selected Steam controller for controllerDevice={assignment.Device.Value}");
        }

        if (device == assignment.Device.Value)
        {
            return new ControllerInputOwnershipResult(
                ShouldProcess: true,
                IsControllerInput: true,
                Device: device,
                Reason: $"matched controllerDevice={assignment.Device.Value}");
        }

        return new ControllerInputOwnershipResult(
            ShouldProcess: false,
            IsControllerInput: true,
            Device: device,
            Reason: $"input device={device} does not match assigned controllerDevice={assignment.Device.Value}");
    }

    public static string FormatLogLine(ControllerInputOwnershipResult result, object? inputEvent)
    {
        var action = GetPropertyValue(inputEvent, "Action");
        var actionSuffix = action is null ? string.Empty : $" action={action}";
        var detailSuffix = FormatInputDetails(inputEvent);
        return $"Controller input ownership: {(result.ShouldProcess ? "allowed" : "suppressed")} device={result.Device?.ToString() ?? "<none>"}{actionSuffix}{detailSuffix} reason={result.Reason}.";
    }

    private static string FormatInputDetails(object? inputEvent)
    {
        if (inputEvent is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        AppendProperty(parts, inputEvent, "ButtonIndex", "button");
        AppendProperty(parts, inputEvent, "Axis", "axis");
        AppendProperty(parts, inputEvent, "AxisValue", "axisValue");
        AppendProperty(parts, inputEvent, "Pressed", "pressed");
        return parts.Count == 0
            ? string.Empty
            : $" {string.Join(" ", parts)}";
    }

    private static void AppendProperty(
        List<string> parts,
        object inputEvent,
        string propertyName,
        string label)
    {
        var value = GetPropertyValue(inputEvent, propertyName);
        if (value is not null)
        {
            parts.Add($"{label}={value}");
        }
    }

    private static bool TryGetControllerDevice(object inputEvent, out int device)
    {
        device = default;
        var typeName = inputEvent.GetType().FullName ?? inputEvent.GetType().Name;
        var isControllerInput = typeName.Contains("Joypad", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("InputEventAction", StringComparison.OrdinalIgnoreCase);
        if (!isControllerInput)
        {
            return false;
        }

        var value = GetPropertyValue(inputEvent, "Device");
        if (value is int intDevice && intDevice >= 0)
        {
            device = intDevice;
            return true;
        }

        return false;
    }

    private static object? GetPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return source.GetType().GetProperty(propertyName, InstanceMembers)?.GetValue(source);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}

public sealed record ControllerInputOwnershipResult(
    bool ShouldProcess,
    bool IsControllerInput,
    int? Device,
    string Reason);

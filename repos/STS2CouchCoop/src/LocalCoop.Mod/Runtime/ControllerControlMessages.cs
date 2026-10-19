using System.Text.Json;

namespace LocalCoop.Mod.Runtime;

public static class BrokerProtocol
{
    public const int CurrentVersion = 4;
}

public static class ControllerControlMessageTypes
{
    public const string Prefix = "localcoop.controller.";
    public const string CollectorStatus = Prefix + "collector-status";
    public const string CollectorLease = Prefix + "collector-lease";
    public const string InventorySnapshot = Prefix + "inventory-snapshot";
    public const string AssignmentSnapshot = Prefix + "assignment-snapshot";
    public const string InputBatch = Prefix + "input-batch";

    public static bool IsControllerControl(string messageType)
    {
        return messageType.StartsWith(Prefix, StringComparison.Ordinal);
    }
}

public enum ControllerSourceKind
{
    SteamInput,
    Godot
}

public enum ControllerConnectionState
{
    Binding,
    Ready
}

public sealed record ControllerSourceDescriptor(
    string SourceId,
    ControllerSourceKind SourceKind,
    string? SteamHandle,
    string? ControllerType,
    int? GodotDeviceId = null,
    int? SteamInputIndex = null,
    int? XInputIndex = null,
    int? VendorId = null,
    int? ProductId = null,
    string? SerialNumber = null,
    string? RawName = null,
    ControllerConnectionState ConnectionState = ControllerConnectionState.Ready);

public sealed record CollectorStatusMessage(
    int ClientIndex,
    bool WindowFocused,
    bool SteamReady,
    bool LogoReady,
    bool ControllerEnabled,
    int ControllerClientCount,
    bool ActionDataReady = false,
    int ConnectedControllerCount = 0,
    int ActiveDigitalActionCount = 0,
    int DigitalActionQueryCount = 0,
    int ActiveAnalogActionCount = 0,
    int AnalogActionQueryCount = 0);

public sealed record CollectorLeaseMessage(
    string? CollectorClientId,
    long LeaseGeneration,
    long AssignmentRevision,
    string Reason);

public sealed record ControllerInventorySnapshotMessage(
    long LeaseGeneration,
    long InventorySequence,
    IReadOnlyList<ControllerSourceDescriptor> Controllers);

public sealed record BrokerControllerAssignmentState(
    string SourceId,
    ControllerSourceKind SourceKind,
    string? SteamHandle,
    string? ControllerType,
    int PlayerSlot,
    string TargetClientId,
    ControllerConnectionState ConnectionState = ControllerConnectionState.Ready);

public sealed record ControllerAssignmentSnapshotMessage(
    long LeaseGeneration,
    long AssignmentRevision,
    IReadOnlyList<BrokerControllerAssignmentState> Assignments);

public enum ControllerInputValueKind
{
    Digital,
    Analog
}

public sealed record ControllerInputValue(
    string SourceId,
    long ControllerSequence,
    string ActionId,
    ControllerInputValueKind Kind,
    bool Pressed = false,
    float X = 0,
    float Y = 0,
    int? TargetClientIndex = null);

public sealed record ControllerInputBatchMessage(
    long LeaseGeneration,
    long AssignmentRevision,
    IReadOnlyList<ControllerInputValue> Inputs);

public static class ControllerControlMessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
    }

    public static T Deserialize<T>(BrokerEnvelope envelope)
    {
        return JsonSerializer.Deserialize<T>(envelope.Payload, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize controller message '{envelope.MessageType}'.");
    }
}

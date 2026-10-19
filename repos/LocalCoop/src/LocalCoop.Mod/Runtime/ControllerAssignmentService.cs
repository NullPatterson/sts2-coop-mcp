namespace LocalCoop.Mod.Runtime;

public static class ControllerAssignmentService
{
    public static ControllerAssignment Resolve(BrokerClientConfig config)
    {
        var playerSlot = config.PlayerSlot ?? config.ClientIndex;
        var controllerDevice = config.InputMode == BrokerClientInputMode.None
            ? BrokerControllerDeviceAssignment.None
            : default;

        return new ControllerAssignment(playerSlot, config.InputMode, controllerDevice, config.ControllerClientCount);
    }
}

public sealed record ControllerAssignment(
    int PlayerSlot,
    BrokerClientInputMode InputMode,
    BrokerControllerDeviceAssignment ControllerDevice,
    int? ControllerClientCount);

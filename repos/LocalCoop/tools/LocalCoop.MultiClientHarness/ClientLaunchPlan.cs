using LocalCoop.Protocol;

namespace LocalCoop.MultiClientHarness;

public sealed record ClientLaunchPlan(IReadOnlyList<ClientLaunchPlanEntry> Clients)
{
    public static ClientLaunchPlan CreateDefault(string sessionId, string brokerHost, int brokerPort)
    {
        return Create(sessionId, brokerHost, brokerPort, clientCount: 4);
    }

    public static ClientLaunchPlan CreateTwoClient(string sessionId, string brokerHost, int brokerPort)
    {
        return Create(sessionId, brokerHost, brokerPort, clientCount: 2);
    }

    public static ClientLaunchPlan Create(
        string sessionId,
        string brokerHost,
        int brokerPort,
        int clientCount,
        IReadOnlyList<BrokerControllerDeviceAssignment>? controllerDevices = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id must not be blank.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(brokerHost))
        {
            throw new ArgumentException("Broker host must not be blank.", nameof(brokerHost));
        }

        if (brokerPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(brokerPort), "Broker port must be 1 through 65535.");
        }

        if (clientCount is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(clientCount), "Client count must be 2 through 4.");
        }

        if (controllerDevices is not null && controllerDevices.Count != clientCount)
        {
            throw new ArgumentException("Controller device override count must match client count.", nameof(controllerDevices));
        }

        var resolvedControllerDevices = controllerDevices
            ?? Enumerable.Range(0, clientCount)
                .Select(BrokerControllerDeviceAssignment.ForDevice)
                .ToArray();
        var controllerClientCount = resolvedControllerDevices.Count(controllerDevice => controllerDevice.Device is not null);

        return new ClientLaunchPlan(
            Enumerable.Range(0, clientCount)
                .Select(index => new ClientLaunchPlanEntry(
                    ClientId: $"client-{index}",
                    ConfigContent: FormatConfig(
                        role: index == 0 ? "host" : "client",
                        clientIndex: index,
                        controllerDevice: resolvedControllerDevices[index],
                        controllerClientCount,
                        brokerHost,
                        brokerPort,
                        sessionId)))
                .ToArray());
    }

    private static string FormatConfig(
        string role,
        int clientIndex,
        BrokerControllerDeviceAssignment controllerDevice,
        int controllerClientCount,
        string brokerHost,
        int brokerPort,
        string sessionId)
    {
        return string.Join(
            Environment.NewLine,
            $"role={role}",
            $"clientIndex={clientIndex}",
            $"playerSlot={FormatPlayerSlot(clientIndex, controllerDevice)}",
            $"inputMode={FormatInputMode(controllerDevice)}",
            $"controllerClientCount={controllerClientCount}",
            $"endpoint={brokerHost}:{brokerPort}",
            $"sessionId={sessionId}",
            string.Empty);
    }

    private static string FormatPlayerSlot(int clientIndex, BrokerControllerDeviceAssignment controllerDevice)
    {
        return clientIndex.ToString();
    }

    private static string FormatInputMode(BrokerControllerDeviceAssignment controllerDevice)
    {
        return controllerDevice.Device is null ? "none" : "auto";
    }

}

public sealed record ClientLaunchPlanEntry(string ClientId, string ConfigContent);

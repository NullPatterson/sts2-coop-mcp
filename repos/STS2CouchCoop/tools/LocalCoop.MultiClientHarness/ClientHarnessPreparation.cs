using LocalCoop.Protocol;

namespace LocalCoop.MultiClientHarness;

public static class ClientHarnessPreparation
{
    public static ClientHarnessPreparationResult Prepare(
        string rootDirectory,
        string gameExecutablePath,
        string sessionId,
        string brokerHost,
        int brokerPort,
        int clientCount,
        string? controllerDevices = null)
    {
        var plan = ClientLaunchPlan.Create(
            sessionId,
            brokerHost,
            brokerPort,
            clientCount,
            ParseControllerDevices(controllerDevices, clientCount));
        var setup = ClientConfigFileSetup.Write(rootDirectory, plan);
        var launchCommands = ClientLaunchInstructionFormatter.FormatPowerShell(setup, gameExecutablePath);

        return new ClientHarnessPreparationResult(
            setup,
            $"dotnet run --project src\\LocalCoop.Broker.Cli -- {sessionId} {brokerPort}",
            launchCommands);
    }

    private static IReadOnlyList<BrokerControllerDeviceAssignment>? ParseControllerDevices(
        string? value,
        int clientCount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != clientCount)
        {
            throw new FormatException("controllerDevices must provide one value per client.");
        }

        return parts.Select(ParseControllerDevice).ToArray();
    }

    private static BrokerControllerDeviceAssignment ParseControllerDevice(string value)
    {
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return BrokerControllerDeviceAssignment.None;
        }

        if (!int.TryParse(value, out var device))
        {
            throw new FormatException("controllerDevices values must be none or integers from 0 through 3.");
        }

        return BrokerControllerDeviceAssignment.ForDevice(device);
    }
}

public sealed record ClientHarnessPreparationResult(
    ClientConfigFileSetupResult ConfigSetup,
    string BrokerCommand,
    IReadOnlyList<string> LaunchCommands);

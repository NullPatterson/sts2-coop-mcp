namespace LocalCoop.MultiClientHarness;

public static class TwoClientHarnessPreparation
{
    public static TwoClientHarnessPreparationResult Prepare(
        string rootDirectory,
        string gameExecutablePath,
        string sessionId,
        string brokerHost,
        int brokerPort)
    {
        var result = ClientHarnessPreparation.Prepare(
            rootDirectory,
            gameExecutablePath,
            sessionId,
            brokerHost,
            brokerPort,
            clientCount: 2);

        return new TwoClientHarnessPreparationResult(
            result.ConfigSetup,
            result.BrokerCommand,
            result.LaunchCommands);
    }
}

public sealed record TwoClientHarnessPreparationResult(
    ClientConfigFileSetupResult ConfigSetup,
    string BrokerCommand,
    IReadOnlyList<string> LaunchCommands);

namespace LocalCoop.MultiClientHarness;

public static class ClientLaunchInstructionFormatter
{
    public static IReadOnlyList<string> FormatPowerShell(
        ClientConfigFileSetupResult setup,
        string gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath))
        {
            throw new ArgumentException("Game executable path must not be blank.", nameof(gameExecutablePath));
        }

        return setup.Clients
            .Select(client =>
                $"$env:LOCALCOOP_CONFIG_DIR='{EscapePowerShellSingleQuoted(client.Directory)}'; " +
                $"Start-Process -FilePath '{EscapePowerShellSingleQuoted(gameExecutablePath)}'")
            .ToArray();
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}

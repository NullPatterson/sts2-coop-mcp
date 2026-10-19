namespace LocalCoop.Mod.Runtime;

public static class BrokerModStartup
{
    public static BrokerModStartupResult Initialize(
        string modDirectory,
        Func<BrokerModeSettings, TransportSeamProbeResult> runProbe)
    {
        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);
        var log = new BrokerEventLog(settings.EventLogPath);
        if (!settings.Enabled)
        {
            var reason = settings.FailureReason is null ? "marker not present" : settings.FailureReason;
            log.Write($"Broker mode disabled: {reason}.");
            return new BrokerModStartupResult(settings);
        }

        log.Write(
            $"Broker mode enabled: clientId={settings.ClientId} role={settings.Config!.Role} " +
            $"endpoint={settings.Config.Host}:{settings.Config.Port} sessionId={settings.Config.SessionId}.");

        var probe = runProbe(settings);
        var reportPath = Path.Combine(modDirectory, $"localcoop-transport-probe-{settings.ClientId}.txt");
        File.WriteAllLines(reportPath, TransportSeamProbeReport.Format(probe));
        log.Write($"Transport probe written: {Path.GetFileName(reportPath)}.");
        return new BrokerModStartupResult(settings);
    }
}

public sealed record BrokerModStartupResult(BrokerModeSettings Settings);


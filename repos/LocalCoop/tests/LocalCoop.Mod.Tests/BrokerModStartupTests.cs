using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerModStartupTests
{
    [TestMethod]
    public void StartupWritesDisabledStatusWhenMarkerIsAbsent()
    {
        var directory = CreateTempDirectory();

        var result = BrokerModStartup.Initialize(directory, _ => new TransportSeamProbeResult([]));

        Assert.IsFalse(result.Settings.Enabled);
        StringAssert.Contains(File.ReadAllText(result.Settings.EventLogPath), "Broker mode disabled");
    }

    [TestMethod]
    public void StartupWritesEnabledStatusAndProbeReport()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, BrokerModeSettings.MarkerFileName), """
            role=host
            clientIndex=0
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        var result = BrokerModStartup.Initialize(directory, _ => new TransportSeamProbeResult(
        [
            new TransportSeamProbeEntry("net game service", "Fake.INetGameService", ["method SendMessage"])
        ]));

        Assert.IsTrue(result.Settings.Enabled);
        var log = File.ReadAllText(result.Settings.EventLogPath);
        StringAssert.Contains(log, "Broker mode enabled");
        StringAssert.Contains(log, "clientId=client-0");

        var report = File.ReadAllText(Path.Combine(directory, "localcoop-transport-probe-client-0.txt"));
        StringAssert.Contains(report, "net game service: Fake.INetGameService");
        StringAssert.Contains(report, "method SendMessage");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalCoopModTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}


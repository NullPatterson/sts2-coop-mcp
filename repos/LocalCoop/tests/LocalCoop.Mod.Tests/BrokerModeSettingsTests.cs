using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerModeSettingsTests
{
    [TestMethod]
    public void LoadFromDirectoryReturnsDisabledWhenMarkerIsAbsent()
    {
        var modDirectory = CreateTempDirectory();

        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);

        Assert.IsFalse(settings.Enabled);
        Assert.IsNull(settings.Config);
        Assert.AreEqual(Path.Combine(modDirectory, "localcoop-events.txt"), settings.EventLogPath);
    }

    [TestMethod]
    public void LoadFromDirectoryParsesMarkerAndDerivesClientIdentity()
    {
        var modDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(modDirectory, BrokerModeSettings.MarkerFileName), """
            role=client
            clientIndex=2
            controllerDevice=2
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(BrokerClientRole.Client, settings.Config?.Role);
        Assert.AreEqual("client-2", settings.ClientId);
        Assert.AreEqual(Path.Combine(modDirectory, "localcoop-client-2-events.txt"), settings.EventLogPath);
        Assert.IsFalse(settings.Config?.ControllerDevice.IsConfigured);
        Assert.IsNull(settings.Config?.ControllerDevice.Device);
    }

    [TestMethod]
    public void LoadFromDirectoryAllowsHostAtNonzeroClientIndex()
    {
        var modDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(modDirectory, BrokerModeSettings.MarkerFileName), """
            role=host
            clientIndex=2
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(BrokerClientRole.Host, settings.Config?.Role);
        Assert.AreEqual(2, settings.Config?.ClientIndex);
        Assert.AreEqual("client-2", settings.ClientId);
    }

    [TestMethod]
    public void LoadFromDirectoryAllowsClientAtClientIndexZero()
    {
        var modDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(modDirectory, BrokerModeSettings.MarkerFileName), """
            role=client
            clientIndex=0
            endpoint=127.0.0.1:38989
            sessionId=local-test
            """);

        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(BrokerClientRole.Client, settings.Config?.Role);
        Assert.AreEqual(0, settings.Config?.ClientIndex);
        Assert.AreEqual("client-0", settings.ClientId);
    }

    [TestMethod]
    public void LoadUsesLocalCoopConfigDirectoryBeforeModDirectory()
    {
        var modDirectory = CreateTempDirectory();
        var configDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(modDirectory, BrokerModeSettings.MarkerFileName), """
            role=host
            clientIndex=0
            endpoint=127.0.0.1:38989
            sessionId=mod-dir-session
            """);
        File.WriteAllText(Path.Combine(configDirectory, BrokerModeSettings.MarkerFileName), """
            role=client
            clientIndex=1
            endpoint=127.0.0.1:38990
            sessionId=config-dir-session
            """);

        var settings = BrokerModeSettings.Load(
            modDirectory,
            name => name == BrokerModeSettings.ConfigDirectoryEnvironmentVariable ? configDirectory : null);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(BrokerClientRole.Client, settings.Config?.Role);
        Assert.AreEqual(1, settings.Config?.ClientIndex);
        Assert.AreEqual("config-dir-session", settings.Config?.SessionId);
        Assert.AreEqual(Path.Combine(modDirectory, "localcoop-client-1-events.txt"), settings.EventLogPath);
    }

    [TestMethod]
    public void LoadFromDirectoryKeepsFailureReasonForMalformedMarker()
    {
        var modDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(modDirectory, BrokerModeSettings.MarkerFileName), "role=client");

        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);

        Assert.IsFalse(settings.Enabled);
        StringAssert.Contains(settings.FailureReason, "Missing broker config key");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalCoopModTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

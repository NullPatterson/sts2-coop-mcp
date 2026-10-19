using LocalCoop.MultiClientHarness;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class ClientConfigFileSetupTests
{
    [TestMethod]
    public void WritesFourClientConfigDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalCoopHarnessTests", Guid.NewGuid().ToString("N"));
        var plan = ClientLaunchPlan.CreateDefault("local-test", "127.0.0.1", 38989);

        var result = ClientConfigFileSetup.Write(root, plan);

        Assert.AreEqual(4, result.Clients.Count);
        foreach (var client in result.Clients)
        {
            Assert.IsTrue(Directory.Exists(client.Directory));
            var marker = Path.Combine(client.Directory, "enable-local-broker.txt");
            Assert.IsTrue(File.Exists(marker));
            var parsed = BrokerClientConfig.Parse(File.ReadAllText(marker));
            Assert.AreEqual(client.ClientIndex, parsed.ClientIndex);
        }
    }
}


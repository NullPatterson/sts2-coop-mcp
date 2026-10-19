using LocalCoop.Broker;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Broker.Tests;

[TestClass]
public sealed class PerClientLogPathTests
{
    [TestMethod]
    public void BuildsDistinctLogPathForEachClient()
    {
        var root = Path.Combine("mods", "LocalCoop");

        var host = PerClientLogPath.For(root, new BrokerClientRegistration("host", BrokerClientRole.Host, 0));
        var client = PerClientLogPath.For(root, new BrokerClientRegistration("client-1", BrokerClientRole.Client, 1));

        Assert.AreEqual(Path.Combine(root, "localcoop-host-0-events.txt"), host);
        Assert.AreEqual(Path.Combine(root, "localcoop-client-1-events.txt"), client);
        Assert.AreNotEqual(host, client);
    }
}

using LocalCoop.MultiClientHarness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class BrokerHarnessSmokeTests
{
    [TestMethod]
    public async Task RegistersFourClientsAndRoutesBroadcastAndDirectMessages()
    {
        var result = await BrokerHarnessSmoke.RunAsync("local-test", CancellationToken.None);

        Assert.AreEqual(4, result.RegisteredClientIds.Count);
        CollectionAssert.AreEquivalent(new[] { "client-1", "client-2", "client-3" }, result.BroadcastTargets.ToArray());
        CollectionAssert.AreEqual(new[] { "client-0" }, result.DirectTargets.ToArray());
    }

    [TestMethod]
    public async Task RegistersTwoClientsAndRoutesLobbySmokeMessages()
    {
        var result = await BrokerHarnessSmoke.RunTwoClientAsync("local-test", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "client-0", "client-1" }, result.RegisteredClientIds.ToArray());
        CollectionAssert.AreEqual(new[] { "client-1" }, result.BroadcastTargets.ToArray());
        CollectionAssert.AreEqual(new[] { "client-0" }, result.DirectTargets.ToArray());
    }
}

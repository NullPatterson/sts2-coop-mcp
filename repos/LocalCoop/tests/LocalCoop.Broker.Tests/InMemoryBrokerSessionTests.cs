using LocalCoop.Broker;
using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Broker.Tests;

[TestClass]
public sealed class InMemoryBrokerSessionTests
{
    [TestMethod]
    public void RegistersExactlyOneHostAndThreeClients()
    {
        var session = new InMemoryBrokerSession("local-test");

        session.Register(new BrokerClientRegistration("client-0", BrokerClientRole.Client, 0));
        session.Register(new BrokerClientRegistration("host", BrokerClientRole.Host, 1));
        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Client, 2));
        session.Register(new BrokerClientRegistration("client-3", BrokerClientRole.Client, 3));

        Assert.AreEqual(4, session.Clients.Count);
        Assert.AreEqual("host", session.HostClientId);
    }

    [TestMethod]
    public void RegistersHostAtNonzeroClientIndex()
    {
        var session = new InMemoryBrokerSession("local-test");

        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Host, 2));

        Assert.AreEqual("client-2", session.HostClientId);
    }

    [TestMethod]
    public void RegistersClientAtClientIndexZero()
    {
        var session = new InMemoryBrokerSession("local-test");

        session.Register(new BrokerClientRegistration("client-0", BrokerClientRole.Client, 0));

        Assert.AreEqual(1, session.Clients.Count);
        Assert.IsNull(session.HostClientId);
    }

    [TestMethod]
    public void RejectsDuplicateHostAtDifferentClientIndex()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-1", BrokerClientRole.Host, 1));

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Host, 2)));

        StringAssert.Contains(exception.Message, "host");
    }

    [TestMethod]
    public void UnregisteringHostClearsHostClientId()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Host, 2));

        session.Unregister("client-2");

        Assert.IsNull(session.HostClientId);
    }

    [TestMethod]
    public void RejectsDuplicateClientIndex()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-1", BrokerClientRole.Client, 1));

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.Register(new BrokerClientRegistration("other-client", BrokerClientRole.Client, 1)));

        StringAssert.Contains(exception.Message, "index 1");
    }

    [TestMethod]
    public void RoutesHostUntargetedEnvelopeAsBroadcast()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-0", BrokerClientRole.Client, 0));
        session.Register(new BrokerClientRegistration("host", BrokerClientRole.Host, 1));
        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Client, 2));

        var routes = session.Route(BrokerEnvelope.Broadcast(
            "local-test",
            "host",
            "LobbyChanged",
            [4, 5, 6],
            sequence: 2));

        CollectionAssert.AreEquivalent(new[] { "client-0", "client-2" }, routes.Select(route => route.TargetClientId).ToArray());
        Assert.IsTrue(routes.All(route => route.Envelope.Sequence == 2));
    }

    [TestMethod]
    public void RoutesClientUntargetedEnvelopeToRegisteredHost()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-0", BrokerClientRole.Client, 0));
        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Host, 2));
        session.Register(new BrokerClientRegistration("client-3", BrokerClientRole.Client, 3));

        var routes = session.Route(BrokerEnvelope.Broadcast(
            "local-test",
            "client-0",
            "ClientLobbyJoinRequest",
            [4, 5, 6],
            sequence: 2));

        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("client-2", routes[0].TargetClientId);
    }

    [TestMethod]
    public void RoutesDirectEnvelopeToTargetOnly()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("host", BrokerClientRole.Host, 0));
        session.Register(new BrokerClientRegistration("client-1", BrokerClientRole.Client, 1));
        session.Register(new BrokerClientRegistration("client-2", BrokerClientRole.Client, 2));

        var routes = session.Route(new BrokerEnvelope(
            SessionId: "local-test",
            SourceClientId: "client-1",
            TargetClientId: "host",
            MessageType: "Ready",
            Payload: [1],
            Sequence: 3));

        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("host", routes[0].TargetClientId);
    }

    [TestMethod]
    public void RejectsClientUntargetedEnvelopeBeforeHostRegisters()
    {
        var session = new InMemoryBrokerSession("local-test");
        session.Register(new BrokerClientRegistration("client-0", BrokerClientRole.Client, 0));

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.Route(BrokerEnvelope.Broadcast(
                "local-test",
                "client-0",
                "ClientLobbyJoinRequest",
                [1],
                sequence: 1)));

        StringAssert.Contains(exception.Message, "host");
    }
}

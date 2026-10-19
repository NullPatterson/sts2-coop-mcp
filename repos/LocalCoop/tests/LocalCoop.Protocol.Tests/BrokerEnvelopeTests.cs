using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Protocol.Tests;

[TestClass]
public sealed class BrokerEnvelopeTests
{
    [TestMethod]
    public void CreatesBroadcastEnvelopeWithCopiedPayload()
    {
        var payload = new byte[] { 1, 2, 3 };
        var envelope = BrokerEnvelope.Broadcast(
            sessionId: "local-test",
            sourceClientId: "client-0",
            messageType: "LobbyReady",
            payload: payload,
            sequence: 7);

        payload[0] = 9;

        Assert.AreEqual("local-test", envelope.SessionId);
        Assert.AreEqual("client-0", envelope.SourceClientId);
        Assert.IsNull(envelope.TargetClientId);
        Assert.AreEqual("LobbyReady", envelope.MessageType);
        Assert.AreEqual(7L, envelope.Sequence);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, envelope.Payload.ToArray());
    }
}

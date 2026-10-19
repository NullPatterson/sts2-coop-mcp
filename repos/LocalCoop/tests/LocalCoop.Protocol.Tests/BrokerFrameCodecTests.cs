using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Protocol.Tests;

[TestClass]
public sealed class BrokerFrameCodecTests
{
    [TestMethod]
    public async Task RoundTripsTransportMessageWithLengthPrefix()
    {
        using var stream = new MemoryStream();
        var message = BrokerTransportMessage.ForEnvelope(BrokerEnvelope.Broadcast(
            "local-test",
            "host",
            "LobbyChanged",
            [1, 2, 3],
            sequence: 5));

        await BrokerFrameCodec.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;

        var decoded = await BrokerFrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(BrokerTransportMessageKind.Envelope, decoded.Kind);
        Assert.AreEqual("LobbyChanged", decoded.Envelope?.MessageType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, decoded.Envelope?.Payload.ToArray());
    }

    [TestMethod]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();

        var decoded = await BrokerFrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.IsNull(decoded);
    }
}


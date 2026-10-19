using System.Text.Json;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace LocalCoop.Mod.Runtime;

public static class BrokerEnvelopeMessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true
    };

    public static BrokerEnvelope ToEnvelope<T>(
        string sessionId,
        string sourceClientId,
        string? targetClientId,
        T message,
        long sequence)
    {
        var payload = message is IPacketSerializable packetSerializable
            ? SerializePacket(packetSerializable)
            : JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        return new BrokerEnvelope(
            sessionId,
            sourceClientId,
            targetClientId,
            typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            payload,
            sequence);
    }

    public static object Deserialize(BrokerEnvelope envelope, Type targetType)
    {
        if (typeof(IPacketSerializable).IsAssignableFrom(targetType))
        {
            return DeserializePacket(envelope.Payload, targetType);
        }

        return JsonSerializer.Deserialize(envelope.Payload, targetType, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize broker envelope as {targetType.FullName}.");
    }

    private static byte[] SerializePacket(IPacketSerializable message)
    {
        var writer = new PacketWriter();
        writer.Reset();
        message.Serialize(writer);
        writer.ZeroByteRemainder();
        return writer.Buffer.Take(writer.BytePosition).ToArray();
    }

    private static object DeserializePacket(byte[] payload, Type targetType)
    {
        if (Activator.CreateInstance(targetType) is not IPacketSerializable message)
        {
            throw new InvalidDataException($"Could not create packet-serializable message {targetType.FullName}.");
        }

        var reader = new PacketReader();
        reader.Reset(payload);
        message.Deserialize(reader);
        return message;
    }
}

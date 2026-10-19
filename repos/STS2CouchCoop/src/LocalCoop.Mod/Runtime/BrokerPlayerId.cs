namespace LocalCoop.Mod.Runtime;

public static class BrokerPlayerId
{
    private const ulong Prefix = 0x4C434F4F00000000UL;

    public static ulong ForClientIndex(int clientIndex)
    {
        if (clientIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(clientIndex), "Client index must be 0 through 3.");
        }

        return Prefix | (uint)clientIndex;
    }

    public static int ToClientIndex(ulong playerId)
    {
        return (playerId & 0xFFFFFFFF00000000UL) == Prefix
            ? (int)(playerId & 0xFFFFFFFFUL)
            : -1;
    }
}

using LocalCoop.Broker;
using LocalCoop.Protocol;
using System.Net;

namespace LocalCoop.MultiClientHarness;

public static class BrokerHarnessSmoke
{
    public static async Task<BrokerHarnessSmokeResult> RunAsync(string sessionId, CancellationToken cancellationToken)
    {
        return await RunAsync(sessionId, clientCount: 4, cancellationToken);
    }

    public static async Task<BrokerHarnessSmokeResult> RunTwoClientAsync(string sessionId, CancellationToken cancellationToken)
    {
        return await RunAsync(sessionId, clientCount: 2, cancellationToken);
    }

    private static async Task<BrokerHarnessSmokeResult> RunAsync(
        string sessionId,
        int clientCount,
        CancellationToken cancellationToken)
    {
        await using var server = new BrokerTcpServer(sessionId, IPAddress.Loopback, port: 0);
        await server.StartAsync(cancellationToken);

        var plan = clientCount == 2
            ? ClientLaunchPlan.CreateTwoClient(sessionId, "127.0.0.1", server.Port)
            : ClientLaunchPlan.CreateDefault(sessionId, "127.0.0.1", server.Port);
        var connections = new List<BrokerClientConnection>();
        try
        {
            foreach (var client in plan.Clients)
            {
                var config = BrokerClientConfig.Parse(client.ConfigContent);
                connections.Add(await BrokerClientConnection.ConnectAsync(config, client.ClientId, cancellationToken));
            }

            await connections[0].SendEnvelopeAsync(BrokerEnvelope.Broadcast(
                sessionId,
                "client-0",
                "SmokeBroadcast",
                [1],
                sequence: 1),
                cancellationToken);

            var broadcastTargets = new List<string>();
            for (var index = 1; index < connections.Count; index++)
            {
                var envelope = await connections[index].ReadEnvelopeAsync(cancellationToken);
                if (envelope?.MessageType == "SmokeBroadcast")
                {
                    broadcastTargets.Add($"client-{index}");
                }
            }

            await connections[1].SendEnvelopeAsync(new BrokerEnvelope(
                sessionId,
                SourceClientId: "client-1",
                TargetClientId: "client-0",
                MessageType: "SmokeDirect",
                Payload: [2],
                Sequence: 2),
                cancellationToken);

            var directTargets = new List<string>();
            var direct = await connections[0].ReadEnvelopeAsync(cancellationToken);
            if (direct?.MessageType == "SmokeDirect")
            {
                directTargets.Add("client-0");
            }

            return new BrokerHarnessSmokeResult(
                plan.Clients.Select(client => client.ClientId).ToArray(),
                broadcastTargets,
                directTargets);
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }
}

public sealed record BrokerHarnessSmokeResult(
    IReadOnlyList<string> RegisteredClientIds,
    IReadOnlyList<string> BroadcastTargets,
    IReadOnlyList<string> DirectTargets);

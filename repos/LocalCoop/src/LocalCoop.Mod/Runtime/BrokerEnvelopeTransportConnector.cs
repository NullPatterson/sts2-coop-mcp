namespace LocalCoop.Mod.Runtime;

public static class BrokerEnvelopeTransportConnector
{
    public static IBrokerEnvelopeTransport ConnectBlocking(
        BrokerClientConfig config,
        string clientId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => ConnectAsync(config, clientId, timeout, cancellationToken),
            cancellationToken).GetAwaiter().GetResult();
    }

    public static async Task<IBrokerEnvelopeTransport> ConnectAsync(
        BrokerClientConfig config,
        string clientId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var connection = await BrokerSharedConnectionRegistry.GetOrConnectAsync(
                    config,
                    clientId,
                    timeoutSource.Token)
                .ConfigureAwait(false);
            return new BrokerClientEnvelopeTransport(connection);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to broker at {config.Host}:{config.Port}.", exception);
        }
    }
}

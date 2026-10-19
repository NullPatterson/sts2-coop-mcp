using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalCoop.Broker;
using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerEnvelopeTransportConnectorTests
{
    [TestMethod]
    public async Task ConnectAsyncTimesOutWhenBrokerAcceptsSocketButDoesNotAcknowledgeRegistration()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
        });

        var config = new BrokerClientConfig(
            BrokerClientRole.Host,
            0,
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            "local-test");
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            BrokerEnvelopeTransportConnector.ConnectAsync(
                config,
                "client-0",
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None));

        stopwatch.Stop();
        listener.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ConnectBlockingCompletesFromNonPumpingSynchronizationContext()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        var config = new BrokerClientConfig(
            BrokerClientRole.Host,
            0,
            "127.0.0.1",
            server.Port,
            "local-test");
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var connectTask = Task.Run(() => BrokerEnvelopeTransportConnector.ConnectBlocking(
                config,
                "client-0",
                TimeSpan.FromSeconds(1),
                timeout.Token));

            Assert.IsTrue(connectTask.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsNotNull(connectTask.Result);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [TestMethod]
    public async Task RuntimeConnectionRaisesPeerRegisteredWhenLaterPeerRegisters()
    {
        await using var server = new BrokerTcpServer("local-test", IPAddress.Loopback, port: 0);
        await server.StartAsync(CancellationToken.None);
        await using var host = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Host, 0, "127.0.0.1", server.Port, "local-test"),
            "client-0",
            CancellationToken.None);
        var peerRegistered = new TaskCompletionSource<BrokerClientRegistrationInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.PeerRegistered += peerRegistered.SetResult;
        using var readCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readTask = Task.Run(async () =>
        {
            try
            {
                await host.ReadEnvelopeAsync(readCancellation.Token);
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
            }
        });

        await using var client = await BrokerClientConnection.ConnectAsync(
            new BrokerClientConfig(BrokerClientRole.Client, 1, "127.0.0.1", server.Port, "local-test"),
            "client-1",
            CancellationToken.None);

        var registration = await peerRegistered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await readCancellation.CancelAsync();
        await readTask;

        Assert.AreEqual("client-1", registration.ClientId);
        Assert.AreEqual(BrokerClientRole.Client, registration.Role);
        Assert.AreEqual(1, registration.ClientIndex);
        Assert.AreEqual("client-1", host.ConnectedPeers.Single().ClientId);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}

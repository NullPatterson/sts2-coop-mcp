using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BrokerBeginRunPatchTests
{
    [TestMethod]
    public void FinalizerSuppressesConcreteHostServiceCastForBrokerHost()
    {
        var logs = new List<string>();
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        var result = BrokerBeginRunPatch.FilterBeginRunException(
            new InvalidCastException("Unable to cast object of type 'LocalCoop.Mod.Runtime.BrokerNetGameService' to type 'MegaCrit.Sts2.Core.Multiplayer.NetHostGameService'."),
            service,
            logs.Add);

        Assert.IsNull(result);
        Assert.IsTrue(logs.Any(log => log.Contains("suppressed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FinalizerSuppressesConcreteHostServiceCastForLobbyInstance()
    {
        var logs = new List<string>();
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        var result = BrokerBeginRunPatch.FilterBeginRunException(
            new InvalidCastException("Unable to cast object of type 'LocalCoop.Mod.Runtime.BrokerNetGameService' to type 'MegaCrit.Sts2.Core.Multiplayer.NetHostGameService'."),
            new FakeBrokerBeginRunOwner(service),
            logs.Add);

        Assert.IsNull(result);
        Assert.IsTrue(logs.Any(log => log.Contains("suppressed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FinalizerKeepsUnrelatedExceptions()
    {
        var exception = new InvalidOperationException("boom");

        var result = BrokerBeginRunPatch.FilterBeginRunException(exception, new object(), _ => { });

        Assert.AreSame(exception, result);
    }

    private sealed class FakeBrokerBeginRunOwner
    {
        private readonly object _netService;

        public FakeBrokerBeginRunOwner(object netService)
        {
            _netService = netService;
        }
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<BrokerEnvelope?>(null);
        }
    }
}

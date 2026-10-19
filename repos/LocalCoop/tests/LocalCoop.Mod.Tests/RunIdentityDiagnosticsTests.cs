using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityDiagnosticsTests
{
    [TestMethod]
    public void DescribeRunSnapshotResolvesSynchronizerLocalPlayerWithoutInvokingLocalPlayerGetter()
    {
        var playerId = BrokerPlayerId.ForClientIndex(1);
        var player = new FakePlayer(playerId);
        var playerCollection = new FakePlayerCollection(player);
        var synchronizer = new FakeSynchronizerWithInstrumentedLocalPlayer(playerId, playerCollection);
        var owner = new FakeRunManagerOwner
        {
            OneOffSynchronizer = synchronizer
        };

        var snapshot = RunIdentityDiagnostics.DescribeRunSnapshot(owner);

        Assert.AreEqual(0, synchronizer.LocalPlayerGetterCount);
        StringAssert.Contains(snapshot, $"_localPlayerId={playerId}/0x{playerId:X16}");
        StringAssert.Contains(snapshot, $"LocalPlayer={typeof(FakePlayer).FullName}");
        StringAssert.Contains(snapshot, $"NetId={playerId}/0x{playerId:X16}");
    }

    [TestMethod]
    public void LogResultDescribesSynchronizerInstanceWithoutInvokingLocalPlayerGetter()
    {
        var playerId = BrokerPlayerId.ForClientIndex(1);
        var player = new FakePlayer(playerId);
        var playerCollection = new FakePlayerCollection(player);
        var synchronizer = new FakeSynchronizerWithInstrumentedLocalPlayer(playerId, playerCollection);
        var logPath = Path.Combine(Path.GetTempPath(), $"localcoop-diagnostics-{Guid.NewGuid():N}.txt");
        var method = typeof(FakeSynchronizerWithInstrumentedLocalPlayer)
            .GetProperty(nameof(FakeSynchronizerWithInstrumentedLocalPlayer.LocalPlayer))!
            .GetMethod!;

        try
        {
            RunIdentityDiagnostics.Configure(
                new BrokerModeSettings(true, null, "client-test", logPath, null),
                new RunIdentityDiagnosticsSettings(true, "test"));

            RunIdentityDiagnostics.LogResult(
                "one-off-local-player",
                method,
                synchronizer,
                [],
                player);

            Assert.AreEqual(0, synchronizer.LocalPlayerGetterCount);
            StringAssert.Contains(File.ReadAllText(logPath), $"_localPlayerId={playerId}/0x{playerId:X16}");
        }
        finally
        {
            RunIdentityDiagnostics.Configure(
                new BrokerModeSettings(false, null, "client-0", logPath, "test complete"),
                new RunIdentityDiagnosticsSettings(false, "test complete"));
        }
    }

    [TestMethod]
    public void PeerInputDiagnosticsAreSampledPerMethodAndPhase()
    {
        RunIdentityDiagnostics.ResetPeerInputDiagnosticsSamplingForTesting();
        var now = new DateTimeOffset(2026, 6, 6, 7, 40, 0, TimeSpan.Zero);
        var method = typeof(FakePeerInputSynchronizer).GetMethod(nameof(FakePeerInputSynchronizer.SyncLocalMousePos))!;

        Assert.IsTrue(RunIdentityDiagnostics.ShouldLogPeerInputDiagnosticsForTesting(
            "peer-input-ownership-enter",
            method,
            now));
        Assert.IsFalse(RunIdentityDiagnostics.ShouldLogPeerInputDiagnosticsForTesting(
            "peer-input-ownership-enter",
            method,
            now.AddMilliseconds(250)));
        Assert.IsTrue(RunIdentityDiagnostics.ShouldLogPeerInputDiagnosticsForTesting(
            "peer-input-ownership-exit",
            method,
            now.AddMilliseconds(250)));
        Assert.IsTrue(RunIdentityDiagnostics.ShouldLogPeerInputDiagnosticsForTesting(
            "peer-input-ownership-enter",
            method,
            now.AddSeconds(1).AddMilliseconds(1)));
    }

    [TestMethod]
    public void DualRoleSuppressionDiagnosticsAreSampledPerMethod()
    {
        RunIdentityDiagnostics.ResetDualRoleSuppressionDiagnosticsSamplingForTesting();
        var now = new DateTimeOffset(2026, 6, 6, 8, 5, 0, TimeSpan.Zero);
        var method = typeof(FakeLocalSelfCoopContext).GetProperty(nameof(FakeLocalSelfCoopContext.IsEnabled))!.GetMethod!;

        Assert.IsTrue(RunIdentityDiagnostics.ShouldLogDualRoleSuppressionDiagnosticsForTesting(method, now));
        Assert.IsFalse(RunIdentityDiagnostics.ShouldLogDualRoleSuppressionDiagnosticsForTesting(
            method,
            now.AddMilliseconds(250)));
        Assert.IsTrue(RunIdentityDiagnostics.ShouldLogDualRoleSuppressionDiagnosticsForTesting(
            method,
            now.AddSeconds(1).AddMilliseconds(1)));
    }

    private sealed class FakeRunManagerOwner
    {
        public FakeNetService NetService { get; } = new();

        public FakeSynchronizerWithInstrumentedLocalPlayer? OneOffSynchronizer { get; init; }
    }

    private sealed class FakeNetService
    {
        public string Type => "Client";

        public ulong NetId => BrokerPlayerId.ForClientIndex(0);
    }

    private sealed class FakeSynchronizerWithInstrumentedLocalPlayer(
        ulong localPlayerId,
        FakePlayerCollection playerCollection)
    {
        private readonly ulong _localPlayerId = localPlayerId;
        private readonly FakePlayerCollection _playerCollection = playerCollection;

        public int LocalPlayerGetterCount { get; private set; }

        public FakePlayer LocalPlayer
        {
            get
            {
                LocalPlayerGetterCount++;
                throw new InvalidOperationException("Diagnostics must not invoke this getter while describing synchronizers.");
            }
        }
    }

    private sealed class FakePlayerCollection(FakePlayer player)
    {
        public FakePlayer GetPlayer(ulong playerId)
        {
            return playerId == player.NetId
                ? player
                : throw new InvalidOperationException("Unexpected player id.");
        }
    }

    private sealed class FakePlayer(ulong netId)
    {
        public ulong NetId { get; } = netId;
    }

    private sealed class FakePeerInputSynchronizer
    {
        public void SyncLocalMousePos()
        {
        }
    }

    private sealed class FakeLocalSelfCoopContext
    {
        public bool IsEnabled => true;
    }
}

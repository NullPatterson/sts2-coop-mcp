using System.Reflection;
using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class LocalCoopPatchInstallerTests
{
    [TestMethod]
    public void DefaultPatchTypesContainCommunicationBridgePatches()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(LocalCoop.Mod.Patches.BrokerClientJoinFlowPatch),
                typeof(LocalCoop.Mod.Patches.BrokerJoinFriendScreenPatch),
                typeof(LocalCoop.Mod.Patches.BrokerHostSteamStartupBypassPatch),
                typeof(LocalCoop.Mod.Patches.BrokerHostENetStartupBypassPatch),
                typeof(LocalCoop.Mod.Patches.BrokerLocalPlayerIdPatch),
                typeof(LocalCoop.Mod.Patches.BrokerLobbyServiceSubstitutionPatch),
                typeof(LocalCoop.Mod.Patches.BrokerBeginRunPatch),
                typeof(LocalCoop.Mod.Patches.BrokerPlayerDisplayNamePlatformPatch),
                typeof(LocalCoop.Mod.Patches.BrokerPlayerDisplayNameNameplatePatch),
                typeof(LocalCoop.Mod.Patches.BaseLibHealthBarForecastCompatibilityPatch),
                typeof(LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches),
                typeof(LocalCoop.Mod.Patches.DynamicControllerStartupPatches),
                typeof(LocalCoop.Mod.Patches.SteamControllerInputFramePatches),
                typeof(LocalCoop.Mod.Patches.ControllerInputBackgroundPollingPatches),
                typeof(LocalCoop.Mod.Patches.DynamicControllerInputFocusScopePatch),
                typeof(LocalCoop.Mod.Patches.DynamicControllerFocusedWindowPatch),
                typeof(LocalCoop.Mod.Patches.ControllerInputOwnershipPatches),
                typeof(LocalCoop.Mod.Patches.SteamControllerInputRuntimeDiagnosticsPatches)
            },
            LocalCoopPatchInstaller.DefaultPatchTypesForTesting.ToArray());
    }

    [TestMethod]
    public void DefaultPatchTypesDoNotContainRunIdentityGameplayOverrides()
    {
        Assert.IsFalse(LocalCoopPatchInstaller.DefaultPatchTypesForTesting.Any(type =>
            type.Name.StartsWith("RunIdentity", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DefaultPatchTypesDoNotContainDiagnosticsPatches()
    {
        Assert.IsFalse(LocalCoopPatchInstaller.DefaultPatchTypesForTesting.Any(type =>
            type != typeof(LocalCoop.Mod.Patches.SteamControllerInputRuntimeDiagnosticsPatches)
            && type.Name.Contains("Diagnostics", StringComparison.Ordinal)
            || type.Name.Contains("Probe", StringComparison.Ordinal)
            || type.Name.Contains("Breadcrumb", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunIdentityDiagnosticsPatchTypesAreOptInOnly()
    {
        Assert.IsTrue(LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.Any());
        Assert.IsFalse(LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.Any(
            LocalCoopPatchInstaller.DefaultPatchTypesForTesting.Contains));
        Assert.IsTrue(LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.All(type =>
            type.Name.Contains("Diagnostics", StringComparison.Ordinal)));
        CollectionAssert.Contains(
            LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.ToArray(),
            typeof(LocalCoop.Mod.Patches.RewardDiagnosticsPatches));
        CollectionAssert.Contains(
            LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.ToArray(),
            typeof(LocalCoop.Mod.Patches.PotionProcurementDiagnosticsPatches));
        CollectionAssert.Contains(
            LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.ToArray(),
            typeof(LocalCoop.Mod.Patches.RunIdentityLocalUiDiagnosticsPatches));
        CollectionAssert.Contains(
            LocalCoopPatchInstaller.RunIdentityDiagnosticsPatchTypesForTesting.ToArray(),
            typeof(LocalCoop.Mod.Patches.PeerInputOwnershipDiagnosticsPatches));
    }

    [TestMethod]
    public void BrokerJoinFriendScreenPatchTargetsJoinFriendsPressedAfterSubmenuPush()
    {
        var target = LocalCoop.Mod.Patches.BrokerJoinFriendScreenPatch.TargetMethod();

        Assert.IsNotNull(target);
        Assert.AreEqual("OnJoinFriendsPressed", target!.Name);
        Assert.AreEqual(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu",
            target.DeclaringType?.FullName);
    }

    [TestMethod]
    public void SteamControllerInputSelectionPatchReplacesNativeUpdateForCanonicalInputModes()
    {
        var configuredController = new BrokerModeSettings(
            Enabled: true,
            Config: new BrokerClientConfig(
                BrokerClientRole.Client,
                ClientIndex: 1,
                Host: "127.0.0.1",
                Port: 38989,
                SessionId: "local-test",
                ControllerDevice: BrokerControllerDeviceAssignment.ForDevice(1)),
            ClientId: "client-1",
            EventLogPath: "events.txt",
            FailureReason: null);
        var noController = configuredController with
        {
            Config = configuredController.Config! with { InputMode = BrokerClientInputMode.None }
        };
        var defaultAutoConfig = configuredController with
        {
            Config = configuredController.Config! with { ControllerDevice = default }
        };
        var disabled = configuredController with { Enabled = false };

        Assert.IsTrue(LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches.ShouldReplaceNativeUpdateControllerConnectionsForTesting(
            configuredController));
        Assert.IsTrue(LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches.ShouldReplaceNativeUpdateControllerConnectionsForTesting(
            noController));
        Assert.IsTrue(LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches.ShouldReplaceNativeUpdateControllerConnectionsForTesting(
            defaultAutoConfig));
        Assert.IsFalse(LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches.ShouldReplaceNativeUpdateControllerConnectionsForTesting(
            disabled));
    }

    [TestMethod]
    public void ControllerInputBackgroundPollingRunsOnlyForAssignedUnfocusedBrokerClient()
    {
        var settings = new BrokerModeSettings(
            Enabled: true,
            Config: new BrokerClientConfig(
                BrokerClientRole.Client,
                ClientIndex: 1,
                Host: "127.0.0.1",
                Port: 38989,
                SessionId: "local-test",
                ControllerDevice: BrokerControllerDeviceAssignment.ForDevice(1)),
            ClientId: "client-1",
            EventLogPath: "events.txt",
            FailureReason: null);
        var assignment = LocalCoopInputRouter.ResolveAssignment(settings.Config!);

        Assert.IsFalse(LocalCoop.Mod.Patches.ControllerInputBackgroundPollingPatches.ShouldPollInBackgroundForTesting(
            settings,
            assignment,
            isFocused: false));
        Assert.IsFalse(LocalCoop.Mod.Patches.ControllerInputBackgroundPollingPatches.ShouldPollInBackgroundForTesting(
            settings,
            assignment,
            isFocused: true));
        Assert.IsFalse(LocalCoop.Mod.Patches.ControllerInputBackgroundPollingPatches.ShouldPollInBackgroundForTesting(
            settings with { Enabled = false },
            assignment,
            isFocused: false));
    }

    [TestMethod]
    public void SteamControllerSelectionRetryIsThrottled()
    {
        var now = DateTimeOffset.UtcNow;
        LocalCoop.Mod.Patches.SteamControllerInputFramePatches.ResetSelectionRetryForTesting();

        Assert.IsTrue(LocalCoop.Mod.Patches.SteamControllerInputFramePatches.TryBeginSelectionRetryForTesting(now));
        Assert.IsFalse(LocalCoop.Mod.Patches.SteamControllerInputFramePatches.TryBeginSelectionRetryForTesting(
            now.AddMilliseconds(999)));
        Assert.IsTrue(LocalCoop.Mod.Patches.SteamControllerInputFramePatches.TryBeginSelectionRetryForTesting(
            now.AddSeconds(1)));
    }

    [TestMethod]
    public void InstallLogsPatchFailuresWithoutThrowing()
    {
        var messages = new List<string>();

        var result = LocalCoopPatchInstaller.Install(
            Assembly.GetExecutingAssembly(),
            [typeof(FailingPatchMarker)],
            messages.Add,
            _ => throw new InvalidOperationException("patch boom"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(messages.Single(), "FailingPatchMarker");
        StringAssert.Contains(messages.Single(), "patch boom");
    }

    private sealed class FailingPatchMarker;
}

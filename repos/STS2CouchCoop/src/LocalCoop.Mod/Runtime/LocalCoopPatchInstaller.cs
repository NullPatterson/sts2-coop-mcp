using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Patches;

namespace LocalCoop.Mod.Runtime;

public static class LocalCoopPatchInstaller
{
    private static readonly Type[] DefaultPatchTypes =
    [
        typeof(BrokerClientJoinFlowPatch),
        typeof(BrokerJoinFriendScreenPatch),
        typeof(BrokerHostSteamStartupBypassPatch),
        typeof(BrokerHostENetStartupBypassPatch),
        typeof(BrokerLocalPlayerIdPatch),
        typeof(BrokerLobbyServiceSubstitutionPatch),
        typeof(BrokerBeginRunPatch),
        typeof(BrokerPlayerDisplayNamePlatformPatch),
        typeof(BrokerPlayerDisplayNameNameplatePatch),
        typeof(BaseLibHealthBarForecastCompatibilityPatch),
        typeof(SteamControllerInputSelectionPatches),
        typeof(DynamicControllerStartupPatches),
        typeof(SteamControllerInputFramePatches),
        typeof(ControllerInputBackgroundPollingPatches),
        typeof(DynamicControllerInputFocusScopePatch),
        typeof(DynamicControllerFocusedWindowPatch),
        typeof(ControllerInputOwnershipPatches),
        typeof(SteamControllerInputRuntimeDiagnosticsPatches)
    ];

    private static readonly Type[] RunIdentityDiagnosticsPatchTypes =
    [
        typeof(RunIdentityLifecycleDiagnosticsPatches),
        typeof(OneOffSynchronizerDiagnosticsPatches),
        typeof(OneOffSynchronizerLocalPlayerDiagnosticsPatch),
        typeof(CardSelectCmdDiagnosticsPatches),
        typeof(CardSelectCmdShouldSelectLocalDiagnosticsPatch),
        typeof(LocalContextIsMeDiagnosticsPatch),
        typeof(PlayerChoiceSynchronizerDiagnosticsPatches),
        typeof(RewardDiagnosticsPatches),
        typeof(RewardStateDiagnosticsPatches),
        typeof(PotionProcurementDiagnosticsPatches),
        typeof(RunIdentityLocalUiDiagnosticsPatches),
        typeof(PeerInputOwnershipDiagnosticsPatches)
    ];

    public static IReadOnlyList<Type> DefaultPatchTypesForTesting => DefaultPatchTypes;

    public static IReadOnlyList<Type> RunIdentityDiagnosticsPatchTypesForTesting => RunIdentityDiagnosticsPatchTypes;

    public static IReadOnlyList<Type> PatchTypesFor(bool includeRunIdentityDiagnostics)
    {
        return includeRunIdentityDiagnostics
            ? DefaultPatchTypes.Concat(RunIdentityDiagnosticsPatchTypes).ToArray()
            : DefaultPatchTypes;
    }

    public static LocalCoopPatchInstallResult Install(
        Assembly assembly,
        Action<string> log,
        Action<Type>? installPatchType = null)
    {
        return Install(assembly, DefaultPatchTypes, log, installPatchType);
    }

    public static LocalCoopPatchInstallResult Install(
        Assembly assembly,
        IReadOnlyList<Type> patchTypes,
        Action<string> log,
        Action<Type>? installPatchType = null)
    {
        Harmony? harmony = null;
        var installer = installPatchType ?? (patchType =>
        {
            harmony ??= new Harmony("localcoop.transport-broker");
            InstallPatchType(patchType, harmony);
        });
        var failures = new List<string>();

        foreach (var patchType in patchTypes)
        {
            try
            {
                installer(patchType);
                log($"Harmony patch installed: {patchType.FullName ?? patchType.Name}.");
            }
            catch (Exception exception)
            {
                var message = $"Harmony patch failed: {patchType.FullName ?? patchType.Name}: {exception.GetType().Name}: {exception.Message}";
                failures.Add(message);
                log(message);
            }
        }

        return new LocalCoopPatchInstallResult(failures.Count == 0, failures);
    }

    private static void InstallPatchType(Type patchType, Harmony harmony)
    {
        harmony.CreateClassProcessor(patchType).Patch();
    }
}

public sealed record LocalCoopPatchInstallResult(bool Success, IReadOnlyList<string> Failures);

using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Modding;

namespace LocalCoop.Mod;

[ModInitializer(nameof(Initialize))]
public static class LocalCoopMod
{
    public const string ModId = "LocalCoop";
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        LocalModAssemblyResolver.Install(typeof(LocalCoopMod).Assembly);

        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        BrokerModStartupResult? startup = null;
        var runIdentityDiagnosticsSettings = new RunIdentityDiagnosticsSettings(false, "mod directory unavailable");
        if (!string.IsNullOrWhiteSpace(modDirectory))
        {
            startup = BrokerModStartup.Initialize(modDirectory, _ => TransportSeamProbe.Run());
            DynamicControllerCoordinator.Initialize(startup.Settings);
            runIdentityDiagnosticsSettings = RunIdentityDiagnosticsSettings.LoadFromDirectory(modDirectory);
            RunIdentityDiagnostics.Configure(startup.Settings, runIdentityDiagnosticsSettings);
        }

        var patchTypes = LocalCoopPatchInstaller.PatchTypesFor(runIdentityDiagnosticsSettings.Enabled);
        if (startup is not null)
        {
            RunIdentityDiagnostics.LogStartupSnapshot(startup.Settings, runIdentityDiagnosticsSettings, patchTypes);
        }

        LocalCoopPatchInstaller.Install(
            typeof(LocalCoopMod).Assembly,
            patchTypes,
            message =>
            {
                if (startup is not null)
                {
                    new BrokerEventLog(startup.Settings.EventLogPath).Write(message);
                }
            });
    }
}

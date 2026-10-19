using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityDiagnosticsSettingsTests
{
    [TestMethod]
    public void LoadReturnsDisabledWithoutMarkerOrEnvironmentFlag()
    {
        var modDirectory = CreateTempDirectory();

        try
        {
            var settings = RunIdentityDiagnosticsSettings.Load(
                modDirectory,
                _ => null);

            Assert.IsFalse(settings.Enabled);
        }
        finally
        {
            Directory.Delete(modDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void LoadReturnsEnabledWhenMarkerExists()
    {
        var modDirectory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(modDirectory, RunIdentityDiagnosticsSettings.MarkerFileName), "enabled");

            var settings = RunIdentityDiagnosticsSettings.Load(
                modDirectory,
                _ => null);

            Assert.IsTrue(settings.Enabled);
        }
        finally
        {
            Directory.Delete(modDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void LoadReturnsEnabledWhenEnvironmentFlagIsTruthy()
    {
        var modDirectory = CreateTempDirectory();

        try
        {
            var settings = RunIdentityDiagnosticsSettings.Load(
                modDirectory,
                name => name == RunIdentityDiagnosticsSettings.EnvironmentVariable ? "1" : null);

            Assert.IsTrue(settings.Enabled);
        }
        finally
        {
            Directory.Delete(modDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalCoopDiagnosticsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

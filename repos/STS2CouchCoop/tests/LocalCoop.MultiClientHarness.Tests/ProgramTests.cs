using LocalCoop.MultiClientHarness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void PrepareClientsReturnsFailureForInvalidClientCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalCoopHarnessTests", Guid.NewGuid().ToString("N"));

        var exitCode = Program.Main(
        [
            "prepare-clients",
            root,
            "5",
            "local-test",
            "38989",
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
        ]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(Directory.Exists(root));
    }
}

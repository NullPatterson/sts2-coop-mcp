using LocalCoop.MultiClientHarness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class GameExecutablePathResolverTests
{
    [TestMethod]
    public void ResolveDefaultFindsGameExecutableBesideLocalCoopModDirectory()
    {
        var baseDirectory = Path.Combine(
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2",
            "LocalCoopMod",
            "tools",
            "LocalCoop.MultiClientHarness",
            "bin",
            "Debug",
            "net9.0");

        var result = GameExecutablePathResolver.ResolveDefault(baseDirectory);

        Assert.AreEqual(
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
            result);
    }
}

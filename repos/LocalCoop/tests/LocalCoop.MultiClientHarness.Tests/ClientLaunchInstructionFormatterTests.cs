using LocalCoop.MultiClientHarness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.MultiClientHarness.Tests;

[TestClass]
public sealed class ClientLaunchInstructionFormatterTests
{
    [TestMethod]
    public void FormatsPowerShellLaunchCommandsWithPerClientConfigDirectories()
    {
        var setup = new ClientConfigFileSetupResult(
        [
            new ClientConfigFileEntry("client-0", 0, @"D:\configs\client-0"),
            new ClientConfigFileEntry("client-1", 1, @"D:\configs\client-1")
        ]);

        var commands = ClientLaunchInstructionFormatter.FormatPowerShell(
            setup,
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe");

        Assert.AreEqual(2, commands.Count);
        StringAssert.Contains(commands[0], "LOCALCOOP_CONFIG_DIR");
        StringAssert.Contains(commands[0], @"D:\configs\client-0");
        StringAssert.Contains(commands[0], "SlayTheSpire2.exe");
        StringAssert.Contains(commands[1], @"D:\configs\client-1");
    }
}

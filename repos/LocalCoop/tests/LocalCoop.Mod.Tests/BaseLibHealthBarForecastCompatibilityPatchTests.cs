using LocalCoop.Mod.Patches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class BaseLibHealthBarForecastCompatibilityPatchTests
{
    private const string RemovedShowsInfiniteHpMessage =
        "Method not found: 'Boolean MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_ShowsInfiniteHp()'.";

    [TestMethod]
    public void TargetMethodsResolveNativeHealthBarRefreshMethodsPatchedByBaseLibForecast()
    {
        var targets = BaseLibHealthBarForecastCompatibilityPatch.TargetMethods()
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "RefreshForeground", "RefreshMiddleground", "RefreshText" },
            targets);
    }

    [TestMethod]
    public void FinalizerSuppressesBaseLibRemovedShowsInfiniteHpForegroundFault()
    {
        var exception = new MissingMethodException(RemovedShowsInfiniteHpMessage);
        var stackTrace = "at BaseLib.Patches.UI.HealthBarForecastPatch.RefreshForegroundOverlay(NHealthBar healthBar)";

        var result = BaseLibHealthBarForecastCompatibilityPatch.FilterBaseLibForecastExceptionForTesting(
            exception,
            stackTrace);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FinalizerSuppressesBaseLibRemovedShowsInfiniteHpMiddlegroundFault()
    {
        var exception = new MissingMethodException(RemovedShowsInfiniteHpMessage);
        var stackTrace = "at BaseLib.Patches.UI.HealthBarForecastPatch.RefreshMiddlegroundOverlay(NHealthBar healthBar)";

        var result = BaseLibHealthBarForecastCompatibilityPatch.FilterBaseLibForecastExceptionForTesting(
            exception,
            stackTrace);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FinalizerSuppressesBaseLibRemovedShowsInfiniteHpTextFault()
    {
        var exception = new MissingMethodException(RemovedShowsInfiniteHpMessage);
        var stackTrace = "at BaseLib.Patches.UI.HealthBarForecastPatch.RefreshTextOverlay(NHealthBar healthBar)";

        var result = BaseLibHealthBarForecastCompatibilityPatch.FilterBaseLibForecastExceptionForTesting(
            exception,
            stackTrace);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FinalizerKeepsUnrelatedMissingMethodFaults()
    {
        var exception = new MissingMethodException("Method not found: 'System.Boolean Other.Missing()'.");
        var stackTrace = "at BaseLib.Patches.UI.HealthBarForecastPatch.RefreshForegroundOverlay(NHealthBar healthBar)";

        var result = BaseLibHealthBarForecastCompatibilityPatch.FilterBaseLibForecastExceptionForTesting(
            exception,
            stackTrace);

        Assert.AreSame(exception, result);
    }

    [TestMethod]
    public void FinalizerKeepsRemovedShowsInfiniteHpFaultsOutsideBaseLibForecastPatch()
    {
        var exception = new MissingMethodException(RemovedShowsInfiniteHpMessage);
        var stackTrace = "at SomeOtherMod.Patch.RefreshForegroundOverlay(NHealthBar healthBar)";

        var result = BaseLibHealthBarForecastCompatibilityPatch.FilterBaseLibForecastExceptionForTesting(
            exception,
            stackTrace);

        Assert.AreSame(exception, result);
    }
}

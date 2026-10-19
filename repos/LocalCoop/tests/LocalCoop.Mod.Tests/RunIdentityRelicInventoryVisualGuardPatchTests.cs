using LocalCoop.Mod.Patches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityRelicInventoryVisualGuardPatchTests
{
    [TestMethod]
    public void RelicInventoryVisualGuardTargetsNativeHiddenIconBoundaries()
    {
        var signatures = RunIdentityRelicInventoryVisualGuardPatch.TargetSignaturesForTesting.ToArray();
        var targetMethods = RunIdentityRelicInventoryVisualGuardPatch.TargetMethods().ToArray();

        CollectionAssert.Contains(
            signatures,
            ("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory", "Add"));
        CollectionAssert.Contains(
            signatures,
            ("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory", "AnimateRelic"));
        CollectionAssert.AreEquivalent(
            new[] { "Add", "AnimateRelic" },
            targetMethods.Select(method => method.Name).ToArray());
    }

    [TestMethod]
    public void RestoresHiddenIconAlphaForMatchingRelicHolder()
    {
        var relic = new object();
        var holder = new FakeRelicHolder(relic, new FakeColor { A = 0f });
        var inventory = new FakeRelicInventory([holder]);

        var restored = RunIdentityRelicInventoryVisualGuardPatch.RestoreRelicIconVisibilityForTesting(inventory, relic);

        Assert.IsTrue(restored);
        Assert.AreEqual(1f, holder.Relic.Icon.Modulate.A);
    }

    [TestMethod]
    public void LeavesVisibleOrNonMatchingRelicHolderUnchanged()
    {
        var matchingRelic = new object();
        var otherRelic = new object();
        var holder = new FakeRelicHolder(matchingRelic, new FakeColor { A = 0.75f });
        var inventory = new FakeRelicInventory([holder]);

        Assert.IsFalse(RunIdentityRelicInventoryVisualGuardPatch.RestoreRelicIconVisibilityForTesting(inventory, otherRelic));
        Assert.AreEqual(0.75f, holder.Relic.Icon.Modulate.A);
        Assert.IsFalse(RunIdentityRelicInventoryVisualGuardPatch.RestoreRelicIconVisibilityForTesting(inventory, matchingRelic));
        Assert.AreEqual(0.75f, holder.Relic.Icon.Modulate.A);
    }

    private sealed class FakeRelicInventory(IReadOnlyList<FakeRelicHolder> relicNodes)
    {
        public IReadOnlyList<FakeRelicHolder> RelicNodes { get; } = relicNodes;
    }

    private sealed class FakeRelicHolder(object model, FakeColor color)
    {
        public FakeRelic Relic { get; } = new(model, color);
    }

    private sealed class FakeRelic(object model, FakeColor color)
    {
        public object Model { get; } = model;

        public FakeIcon Icon { get; } = new(color);
    }

    private sealed class FakeIcon(FakeColor color)
    {
        public FakeColor Modulate { get; set; } = color;
    }

    private struct FakeColor
    {
        public float A;
    }
}

using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class DynamicControllerWindowFocusPatchesTests
{
    [TestInitialize]
    public void Initialize()
    {
        DynamicControllerWindowFocusOverride.ResetForTesting();
    }

    [TestCleanup]
    public void Cleanup()
    {
        DynamicControllerWindowFocusOverride.ResetForTesting();
    }

    [TestMethod]
    public void PreservesGameFocusResultOutsideGeneratedInputScope()
    {
        Assert.IsFalse(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(false));
        Assert.IsTrue(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(true));
    }

    [TestMethod]
    public void TreatsWindowAsFocusedInsideGeneratedInputScope()
    {
        DynamicControllerWindowFocusOverride.Enter();

        Assert.IsTrue(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(false));
    }

    [TestMethod]
    public void KeepsNestedGeneratedInputScopesBalanced()
    {
        DynamicControllerWindowFocusOverride.Enter();
        DynamicControllerWindowFocusOverride.Enter();

        DynamicControllerWindowFocusOverride.Exit();
        Assert.IsTrue(DynamicControllerWindowFocusOverride.IsActive);

        DynamicControllerWindowFocusOverride.Exit();
        Assert.IsFalse(DynamicControllerWindowFocusOverride.IsActive);
    }

    [TestMethod]
    public void GeneratedInputKeepsNativeUiHandlersFocusedOnBackgroundClient()
    {
        var generated = new object();
        SteamControllerInputSelection.RegisterGeneratedInputEvents([generated]);

        DynamicControllerInputFocusScopePatch.PrefixForTesting(
            generated,
            dynamicControllerEnabled: true,
            out var entered);

        Assert.IsTrue(entered);
        Assert.IsTrue(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(false));

        DynamicControllerInputFocusScopePatch.Finalizer(null, entered);
        Assert.IsFalse(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(false));
    }

    [TestMethod]
    public void PhysicalInputDoesNotOverrideBackgroundFocus()
    {
        DynamicControllerInputFocusScopePatch.PrefixForTesting(
            new object(),
            dynamicControllerEnabled: true,
            out var entered);

        Assert.IsFalse(entered);
        Assert.IsFalse(DynamicControllerFocusedWindowPatch.ApplyFocusOverrideForTesting(false));
    }

    [TestMethod]
    public void ActivatesGameControllerModeAtAssignmentTime()
    {
        var manager = new FakeControllerManager();

        var activated = DynamicControllerModeActivation.TryActivate(manager, out var reason);

        Assert.IsTrue(activated, reason);
        Assert.IsTrue(manager.IsUsingController);
        Assert.AreEqual(1, manager.ScreenContextRefreshes);
        Assert.AreEqual(1, manager.ControllerDetectedSignals);
        Assert.AreEqual(1, manager.ControlModeChanges);
    }

    [TestMethod]
    public void FocusPatchesResolveCurrentGameMethods()
    {
        var inputHandlers = DynamicControllerInputFocusScopePatch.TargetMethods().ToArray();
        var focusedWindow = DynamicControllerFocusedWindowPatch.TargetMethod();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager._Input",
                "MegaCrit.Sts2.Core.Nodes.CommonUi.NInputManager._UnhandledInput",
                "MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager._UnhandledInput"
            },
            inputHandlers
                .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
                .ToArray());
        Assert.IsNotNull(focusedWindow);
        Assert.AreEqual("IsGameFocusedWindow", focusedWindow!.Name);
        Assert.AreEqual("MegaCrit.Sts2.Core.Nodes.NGame", focusedWindow.DeclaringType?.FullName);
    }

    private sealed class FakeControllerManager
    {
        public bool IsUsingController { get; private set; }

        public int ScreenContextRefreshes { get; private set; }

        public int ControllerDetectedSignals { get; private set; }

        public int ControlModeChanges { get; private set; }

        private void OnScreenContextChanged()
        {
            ScreenContextRefreshes++;
        }

        private void EmitSignalControllerDetected()
        {
            ControllerDetectedSignals++;
        }

        private void ControlModeChanged()
        {
            ControlModeChanges++;
        }
    }
}

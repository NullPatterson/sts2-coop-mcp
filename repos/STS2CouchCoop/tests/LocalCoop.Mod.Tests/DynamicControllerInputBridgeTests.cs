using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class DynamicControllerInputBridgeTests
{
    [TestInitialize]
    public void Reset()
    {
        Godot.Input.Parsed.Clear();
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        DynamicControllerInputBridge.ResetForTesting();
    }

    [TestMethod]
    public void DispatchesTheGameManagersCurrentControllerMapping()
    {
        var manager = new FakeInputManager();
        var source = new Godot.InputEventAction
        {
            Action = "controller_d_pad_south",
            Pressed = true,
            Strength = 1
        };

        var dispatched = DynamicControllerInputBridge.DispatchMappedActions(manager, source);

        Assert.AreEqual(1, dispatched);
        var mapped = (Godot.InputEventAction)Godot.Input.Parsed.Single();
        Assert.AreEqual("ui_down", mapped.Action);
        Assert.IsTrue(mapped.Pressed);
        Assert.IsTrue(SteamControllerInputSelection.IsGeneratedInputEvent(mapped));
    }

    [TestMethod]
    public void DispatchesReleasesAndIgnoresUnmappedActions()
    {
        var manager = new FakeInputManager();
        var release = new Godot.InputEventAction
        {
            Action = "controller_face_button_south",
            Pressed = false
        };
        var unmapped = new Godot.InputEventAction
        {
            Action = "controller_left_trigger",
            Pressed = true
        };

        var released = DynamicControllerInputBridge.DispatchMappedActions(manager, release);
        var ignored = DynamicControllerInputBridge.DispatchMappedActions(manager, unmapped);

        Assert.AreEqual(1, released);
        Assert.AreEqual(0, ignored);
        var mapped = (Godot.InputEventAction)Godot.Input.Parsed.Single();
        Assert.AreEqual("ui_select", mapped.Action);
        Assert.IsFalse(mapped.Pressed);
    }

    [TestMethod]
    public void DispatchesThroughRememberedInputManagerBeforeUnhandledInput()
    {
        DynamicControllerInputBridge.RememberInputManager(new FakeInputManager());
        var source = new Godot.InputEventAction
        {
            Action = "controller_d_pad_south",
            Pressed = true,
            Strength = 1
        };

        var dispatched = DynamicControllerInputBridge.DispatchMappedActions(source);

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual("ui_down", ((Godot.InputEventAction)Godot.Input.Parsed.Single()).Action);
    }

    private sealed class FakeInputManager
    {
        public readonly Dictionary<string, string> _controllerInputMap = new(StringComparer.Ordinal)
        {
            ["ui_down"] = "controller_d_pad_south",
            ["ui_select"] = "controller_face_button_south"
        };
    }
}

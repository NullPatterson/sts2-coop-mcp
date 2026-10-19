using System.Reflection;
using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class LocalCoopInputRouterTests
{
    [TestMethod]
    public void ResolvesNoneAssignmentAsKeyboardOnly()
    {
        var assignment = LocalCoopInputRouter.ResolveAssignment(new BrokerClientConfig(
            BrokerClientRole.Client,
            ClientIndex: 2,
            Host: "127.0.0.1",
            Port: 38989,
            SessionId: "local-test",
            PlayerSlot: 2,
            InputMode: BrokerClientInputMode.None));

        Assert.AreEqual(2, assignment.ClientIndex);
        Assert.AreEqual(2, assignment.PlayerSlot);
        Assert.AreEqual(BrokerClientInputMode.None, assignment.InputMode);
        Assert.AreEqual(ControllerInputSource.None, assignment.SelectedSource);
        Assert.IsNull(assignment.ControllerDevice.Device);
    }

    [TestMethod]
    public void MapsSteamControllerActionToCanonicalAction()
    {
        Assert.AreEqual(
            CanonicalInputAction.Down,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_d_pad_south", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Select,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_face_button_south", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Confirm,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_face_button_north", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.TopPanel,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_face_button_west", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.TabLeft,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_left_bumper", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.TabRight,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_right_bumper", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Settings,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_start_button", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Map,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_select_button", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Map,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("ui_controller_touch_pad", device: 0)));
        Assert.AreEqual(
            CanonicalInputAction.Peek,
            LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("controller_joystick_press", device: 0)));
        Assert.IsNull(LocalCoopInputRouter.TryMapCanonicalAction(new FakeInputEventAction("ui_down", device: 0)));
    }

    [TestMethod]
    public void DeliversMappedInputEventThroughGodotParseWhenAvailable()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        Godot.Input.Parsed.Clear();
        var sink = new RecordingSink();
        var method = typeof(RecordingSink).GetMethod(nameof(RecordingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new ParsedInputEvent("controller_d_pad_south", device: 0),
            out var delivery);

        Assert.IsTrue(delivered);
        Assert.IsTrue(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Down, delivery.CanonicalAction);
        Assert.AreEqual("ui_down", delivery.TargetAction);
        Assert.AreEqual("parsed", delivery.Reason);
        Assert.AreEqual(0, sink.Handled.Count);
        Assert.AreEqual(1, Godot.Input.Parsed.Count);
        var parsed = (ParsedInputEvent)Godot.Input.Parsed[0];
        Assert.AreEqual("ui_down", parsed.Action);
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(parsed));
    }

    [TestMethod]
    public void DeliversSteamSouthButtonThroughUiSelectForPatchedControllerMap()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        Godot.Input.Parsed.Clear();
        var sink = new RecordingSink();
        var method = typeof(RecordingSink).GetMethod(nameof(RecordingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new ParsedInputEvent("controller_face_button_south", device: 0),
            out var delivery);

        Assert.IsTrue(delivered);
        Assert.IsTrue(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Select, delivery.CanonicalAction);
        Assert.AreEqual("ui_select", delivery.TargetAction);
        var parsed = (ParsedInputEvent)Godot.Input.Parsed[0];
        Assert.AreEqual("ui_select", parsed.Action);
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(parsed));
    }

    [TestMethod]
    public void DeliversSteamNorthButtonThroughUiAcceptForPatchedControllerMap()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        Godot.Input.Parsed.Clear();
        var sink = new RecordingSink();
        var method = typeof(RecordingSink).GetMethod(nameof(RecordingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new ParsedInputEvent("controller_face_button_north", device: 0),
            out var delivery);

        Assert.IsTrue(delivered);
        Assert.IsTrue(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Confirm, delivery.CanonicalAction);
        Assert.AreEqual("ui_accept", delivery.TargetAction);
        var parsed = (ParsedInputEvent)Godot.Input.Parsed[0];
        Assert.AreEqual("ui_accept", parsed.Action);
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(parsed));
    }

    [TestMethod]
    public void DeliversXboxBumperAndMenuAliasesThroughGodotParse()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        Godot.Input.Parsed.Clear();
        var sink = new RecordingSink();
        var method = typeof(RecordingSink).GetMethod(nameof(RecordingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        AssertXboxAliasDelivery(
            sink,
            method,
            "controller_left_bumper",
            CanonicalInputAction.TabLeft,
            "mega_view_deck_and_tab_left");
        AssertXboxAliasDelivery(
            sink,
            method,
            "controller_right_bumper",
            CanonicalInputAction.TabRight,
            "mega_view_exhaust_pile_and_tab_right");
        AssertXboxAliasDelivery(
            sink,
            method,
            "controller_start_button",
            CanonicalInputAction.Settings,
            "mega_pause_and_back");

        Assert.AreEqual(0, sink.Handled.Count);
    }

    [TestMethod]
    public void DoesNotTreatNonAuthoritativeDirectSinkAsDelivery()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        var sink = new RecordingSink();
        var method = typeof(RecordingSink).GetMethod(nameof(RecordingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new FakeInputEventAction("controller_d_pad_south", device: 0),
            out var delivery);

        Assert.IsFalse(delivered);
        Assert.IsFalse(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Down, delivery.CanonicalAction);
        Assert.AreEqual("ui_down", delivery.TargetAction);
        StringAssert.Contains(delivery.Reason, "non-authoritative-sink");
        Assert.AreEqual(0, sink.Handled.Count);
    }

    [TestMethod]
    public void DeliversMappedInputEventDirectlyToAuthoritativeSinkWhenParseUnavailable()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        var sink = new NInputManager();
        var method = typeof(NInputManager).GetMethod(nameof(NInputManager._UnhandledInput), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new FakeInputEventAction("controller_d_pad_south", device: 0),
            out var delivery);

        Assert.IsTrue(delivered);
        Assert.IsTrue(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Down, delivery.CanonicalAction);
        Assert.AreEqual("ui_down", delivery.TargetAction);
        Assert.AreEqual("direct-authoritative", delivery.Reason);
        Assert.AreEqual(1, sink.Handled.Count);
        Assert.AreEqual("ui_down", sink.Handled[0].Action);
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(sink.Handled[0]));
    }

    [TestMethod]
    public void FailedMappedSinkDeliveryIsReportedWithoutClaimingDelivery()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        var sink = new ThrowingSink();
        var method = typeof(ThrowingSink).GetMethod(nameof(ThrowingSink.Handle), BindingFlags.Instance | BindingFlags.Public)!;

        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new FakeInputEventAction("controller_d_pad_south", device: 0),
            out var delivery);

        Assert.IsFalse(delivered);
        Assert.IsFalse(delivery.Delivered);
        Assert.AreEqual(CanonicalInputAction.Down, delivery.CanonicalAction);
        Assert.AreEqual("ui_down", delivery.TargetAction);
        StringAssert.Contains(delivery.Reason, "allowing original");
    }

    private sealed class RecordingSink
    {
        public List<FakeInputEventAction> Handled { get; } = [];

        public void Handle(FakeInputEventAction inputEvent)
        {
            Handled.Add(inputEvent);
        }
    }

    private static void AssertXboxAliasDelivery(
        object sink,
        MethodBase method,
        string sourceAction,
        CanonicalInputAction canonicalAction,
        string targetAction)
    {
        var delivered = LocalCoopInputRouter.TryDeliverCanonicalInputToSink(
            sink,
            method,
            new ParsedInputEvent(sourceAction, device: 0),
            out var delivery);

        Assert.IsTrue(delivered, sourceAction);
        Assert.IsTrue(delivery.Delivered, sourceAction);
        Assert.AreEqual(canonicalAction, delivery.CanonicalAction, sourceAction);
        Assert.AreEqual(targetAction, delivery.TargetAction, sourceAction);
        var parsed = (ParsedInputEvent)Godot.Input.Parsed[^1];
        Assert.AreEqual(targetAction, parsed.Action, sourceAction);
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(parsed), sourceAction);
    }

    private sealed class NInputManager
    {
        public List<FakeInputEventAction> Handled { get; } = [];

        public void _UnhandledInput(FakeInputEventAction inputEvent)
        {
            Handled.Add(inputEvent);
        }
    }

    private sealed class ThrowingSink
    {
        public void Handle(FakeInputEventAction inputEvent)
        {
            throw new InvalidOperationException("sink failed");
        }
    }

    private sealed class FakeInputEventAction(string action, int device, bool pressed = true)
    {
        public string Action { get; set; } = action;
        public int Device { get; set; } = device;
        public bool Pressed { get; set; } = pressed;

        public FakeInputEventAction Duplicate()
        {
            return new FakeInputEventAction(Action, Device, Pressed);
        }
    }

    public sealed class ParsedInputEvent(string action, int device, bool pressed = true)
    {
        public string Action { get; set; } = action;
        public int Device { get; set; } = device;
        public bool Pressed { get; set; } = pressed;

        public ParsedInputEvent Duplicate()
        {
            return new ParsedInputEvent(Action, Device, Pressed);
        }
    }
}

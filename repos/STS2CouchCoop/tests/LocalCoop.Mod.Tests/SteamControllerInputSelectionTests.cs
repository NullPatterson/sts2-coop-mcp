using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class SteamControllerInputSelectionTests
{
    [TestMethod]
    public void SelectsConfiguredControllerOrdinal()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "second"],
            BrokerControllerDeviceAssignment.ForDevice(1));

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("second", selection.Handle);
    }

    [TestMethod]
    public void DoesNotSelectWhenControllerDeviceIsUnconfigured()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "second"],
            default(BrokerControllerDeviceAssignment));

        Assert.IsFalse(selection.Selected);
    }

    [TestMethod]
    public void DoesNotSelectWhenConfiguredOrdinalIsMissing()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first"],
            BrokerControllerDeviceAssignment.ForDevice(1));

        Assert.IsFalse(selection.Selected);
        Assert.AreEqual(1, selection.Index);
    }

    [TestMethod]
    public void KeepsKnownControllerHandleWhenSteamHandleOrderChanges()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "third", "second"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second");

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("second", selection.Handle);
    }

    [TestMethod]
    public void LegacyConfigsFallBackToConfiguredSlotWhenKnownControllerHandleIsMissing()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "third", "fourth"],
            BrokerControllerDeviceAssignment.ForDevice(2),
            "second");

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(2, selection.Index);
        Assert.AreEqual("fourth", selection.Handle);
        StringAssert.Contains(selection.Reason, "reacquired configured playerSlot=2");
    }

    [TestMethod]
    public void DisconnectWithoutSpareLeavesControllerInactive()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "other"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2);

        Assert.IsFalse(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        StringAssert.Contains(selection.Reason, "no spare controller");
    }

    [TestMethod]
    public void DisconnectWithSpareAssignsSpareController()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "other", "extra"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2);

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("extra", selection.Handle);
        Assert.IsFalse(selection.RememberHandle);
        StringAssert.Contains(selection.Reason, "assigned spare controller");
    }

    [TestMethod]
    public void DisconnectSkipsAlreadyClaimedSpareController()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "other", "claimed-extra", "free-extra"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2,
            unavailableControllerHandles: new HashSet<string>(StringComparer.Ordinal)
            {
                "claimed-extra"
            });

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("free-extra", selection.Handle);
        Assert.IsFalse(selection.RememberHandle);
        StringAssert.Contains(selection.Reason, "handleIndex=3");
    }

    [TestMethod]
    public void DisconnectStaysInactiveWhenAllSparesAreAlreadyClaimed()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "other", "claimed-extra"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2,
            unavailableControllerHandles: new HashSet<string>(StringComparer.Ordinal)
            {
                "claimed-extra"
            });

        Assert.IsFalse(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        StringAssert.Contains(selection.Reason, "spare controller already claimed");
    }

    [TestMethod]
    public void DisconnectWithMultipleSparesPrefersHandleBeyondControllerClients()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "extra-a", "extra-b"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2);

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("extra-b", selection.Handle);
        StringAssert.Contains(selection.Reason, "handleIndex=2");
    }

    [TestMethod]
    public void ReconnectedKnownControllerReclaimsOriginalClient()
    {
        var selection = SteamControllerInputSelection.ChooseControllerHandle(
            ["first", "extra", "second"],
            BrokerControllerDeviceAssignment.ForDevice(1),
            "second",
            controllerClientCount: 2);

        Assert.IsTrue(selection.Selected);
        Assert.AreEqual(1, selection.Index);
        Assert.AreEqual("second", selection.Handle);
        Assert.IsTrue(selection.RememberHandle);
        StringAssert.Contains(selection.Reason, "retained previous selected Steam controller handle");
    }

    [TestMethod]
    public void NativeFallbackMapsPlayerSlotToSparseGodotJoypadDevice()
    {
        Godot.Input.ConnectedJoypads.Clear();
        Godot.Input.ConnectedJoypads.AddRange([0, 3]);

        var assignment = SteamControllerInputSelection.ResolveNativeFallbackAssignmentForTesting(
            BrokerControllerDeviceAssignment.ForDevice(1),
            controllerClientCount: 4);

        Assert.AreEqual(3, assignment.Device);
    }

    [TestMethod]
    public void NativeFallbackSuppressesUnconnectedDefaultPlayerSlots()
    {
        Godot.Input.ConnectedJoypads.Clear();
        Godot.Input.ConnectedJoypads.AddRange([0, 3]);

        var assignment = SteamControllerInputSelection.ResolveNativeFallbackAssignmentForTesting(
            BrokerControllerDeviceAssignment.ForDevice(3),
            controllerClientCount: 4);

        Assert.IsTrue(assignment.IsConfigured);
        Assert.IsNull(assignment.Device);
    }

    [TestMethod]
    public void NativeFallbackKeepsConfiguredDeviceWhenGodotJoypadListIsEmpty()
    {
        Godot.Input.ConnectedJoypads.Clear();

        var assignment = SteamControllerInputSelection.ResolveNativeFallbackAssignmentForTesting(
            BrokerControllerDeviceAssignment.ForDevice(3),
            controllerClientCount: 4);

        Assert.AreEqual(3, assignment.Device);
    }

    [TestMethod]
    public void NativeFallbackKeepsExplicitNativeDeviceOverrideOutsideControllerClientCount()
    {
        Godot.Input.ConnectedJoypads.Clear();
        Godot.Input.ConnectedJoypads.AddRange([0, 3]);

        var assignment = SteamControllerInputSelection.ResolveNativeFallbackAssignmentForTesting(
            BrokerControllerDeviceAssignment.ForDevice(3),
            controllerClientCount: 2);

        Assert.AreEqual(3, assignment.Device);
    }

    [TestMethod]
    public void TracksSelectedControllerDevice()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.SetSelectedControllerDeviceForTesting(2);

        Assert.IsTrue(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(2)));
        SteamControllerInputSelection.SetSelectedControllerDeviceForTesting(0);
        Assert.IsTrue(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        SteamControllerInputSelection.SetSelectedControllerDeviceForTesting(2);
        Assert.IsFalse(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(1)));
        Assert.IsFalse(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.None));
    }

    [TestMethod]
    public void DetectsAlreadyAppliedSelectionByControllerDeviceAndHandle()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.SetSelectedControllerDeviceForTesting(2);
        var selectedHandle = new object();
        var differentHandle = new object();

        Assert.IsTrue(SteamControllerInputSelection.IsSelectionAlreadyAppliedForTesting(
            BrokerControllerDeviceAssignment.ForDevice(2),
            selectedHandle,
            selectedHandle));
        Assert.IsFalse(SteamControllerInputSelection.IsSelectionAlreadyAppliedForTesting(
            BrokerControllerDeviceAssignment.ForDevice(1),
            selectedHandle,
            selectedHandle));
        Assert.IsFalse(SteamControllerInputSelection.IsSelectionAlreadyAppliedForTesting(
            BrokerControllerDeviceAssignment.ForDevice(2),
            differentHandle,
            selectedHandle));
        Assert.IsFalse(SteamControllerInputSelection.IsSelectionAlreadyAppliedForTesting(
            BrokerControllerDeviceAssignment.None,
            selectedHandle,
            selectedHandle));
    }

    [TestMethod]
    public void TracksGeneratedInputEventsByReference()
    {
        var generated = new object();
        var sameShapeButDifferentReference = new object();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.RegisterGeneratedInputEvents([generated]);

        Assert.IsTrue(SteamControllerInputSelection.IsGeneratedInputEvent(generated));
        Assert.IsFalse(SteamControllerInputSelection.IsGeneratedInputEvent(sameShapeButDifferentReference));
    }

    [TestMethod]
    public void ConsumesUiCompanionActionForSelectedSteamControllerAction()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true));

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_left", device: 0)));
    }

    [TestMethod]
    public void RegistersUiCompanionForRepeatedPressesFromSameGeneratedInputEvent()
    {
        var generatedInputEvent = new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(generatedInputEvent);
        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(generatedInputEvent);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0)));
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0)));
    }

    [TestMethod]
    public void DoesNotCreateUiCompanionActionForButtonRelease()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: false));

        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0)));
    }

    [TestMethod]
    public void AcceptedUiCompanionReleaseEventStaysAcceptedByReference()
    {
        var uiCompanionRelease = new FakeInputEventAction("ui_select", device: 0, pressed: false);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.AcceptGeneratedUiCompanionInputEventForTesting(uiCompanionRelease);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(uiCompanionRelease));
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(uiCompanionRelease));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_select", device: 0, pressed: false)));
    }

    [TestMethod]
    public void AcceptedUiCompanionEventStaysAcceptedByReference()
    {
        var uiCompanionInputEvent = new FakeInputEventAction("ui_right", device: 0, pressed: true);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true));

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(uiCompanionInputEvent));
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(uiCompanionInputEvent));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(new FakeInputEventAction("ui_right", device: 0, pressed: true)));
    }

    [TestMethod]
    public void ExpiresPendingUiCompanionActions()
    {
        var now = new DateTimeOffset(2026, 5, 29, 19, 0, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(
            new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true),
            now);

        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(
            new FakeInputEventAction("ui_right", device: 0, pressed: true),
            now.AddMilliseconds(251)));
    }

    [TestMethod]
    public void MismatchedUiCompanionActionDoesNotConsumePendingMatch()
    {
        var now = new DateTimeOffset(2026, 5, 29, 19, 0, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedUiCompanionAction(
            new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true),
            now);

        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(
            new FakeInputEventAction("ui_left", device: 0, pressed: true),
            now.AddMilliseconds(10)));
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedUiCompanionInputEvent(
            new FakeInputEventAction("ui_right", device: 0, pressed: true),
            now.AddMilliseconds(20)));
    }

    [TestMethod]
    public void CreatesTranslatedUiInputEventWithoutMutatingOriginalSteamControllerAction()
    {
        var steamControllerAction = new FakeInputEventAction("controller_d_pad_east", device: 0, pressed: true);

        Assert.IsTrue(SteamControllerInputSelection.TryCreateUiCompanionInputEvent(
            steamControllerAction,
            out var uiCompanionInputEvent));

        var companion = (FakeInputEventAction)uiCompanionInputEvent!;
        Assert.AreNotSame(steamControllerAction, companion);
        Assert.AreEqual("controller_d_pad_east", steamControllerAction.Action);
        Assert.AreEqual("ui_right", companion.Action);
        Assert.AreEqual(0, companion.Device);
        Assert.IsTrue(companion.Pressed);
    }

    [TestMethod]
    public void CreatesTranslatedUiSelectEventForSteamSouthButtonRelease()
    {
        var steamControllerRelease = new FakeInputEventAction("controller_face_button_south", device: 0, pressed: false);

        Assert.IsTrue(SteamControllerInputSelection.TryCreateUiCompanionInputEvent(
            steamControllerRelease,
            out var uiCompanionInputEvent));

        var companion = (FakeInputEventAction)uiCompanionInputEvent!;
        Assert.AreEqual("ui_select", companion.Action);
        Assert.AreEqual(0, companion.Device);
        Assert.IsFalse(companion.Pressed);
    }

    [TestMethod]
    public void CreatesTranslatedUiAcceptEventForSteamNorthButton()
    {
        var steamControllerConfirm = new FakeInputEventAction("controller_face_button_north", device: 0, pressed: true);

        Assert.IsTrue(SteamControllerInputSelection.TryCreateUiCompanionInputEvent(
            steamControllerConfirm,
            out var uiCompanionInputEvent));

        var companion = (FakeInputEventAction)uiCompanionInputEvent!;
        Assert.AreEqual("ui_accept", companion.Action);
        Assert.AreEqual(0, companion.Device);
        Assert.IsTrue(companion.Pressed);
    }

    [TestMethod]
    public void DoesNotCreateTranslatedUiInputEventForUnknownSteamControllerAction()
    {
        Assert.IsFalse(SteamControllerInputSelection.TryCreateUiCompanionInputEvent(
            new FakeInputEventAction("controller_unknown", device: 0, pressed: true),
            out var uiCompanionInputEvent));
        Assert.IsNull(uiCompanionInputEvent);
    }

    [TestMethod]
    public void DerivesNativeGeneratedActionMapFromNativeControllerConfig()
    {
        var config = new FakeControllerConfig(
            new Dictionary<string, string>
            {
                ["Top_Panel"] = "controller_face_button_west",
                ["Select"] = "controller_face_button_south"
            },
            new Dictionary<string, string>
            {
                ["mega_top_panel"] = "controller_face_button_west",
                ["mega_select"] = "controller_face_button_south"
            });

        var map = SteamControllerInputSelection.CreateNativeGeneratedActionMapForTesting(config);

        Assert.AreEqual("mega_top_panel", map["controller_face_button_west"]);
        Assert.AreEqual("mega_select", map["controller_face_button_south"]);
    }

    [TestMethod]
    public void DerivesPs4TouchpadNativeGeneratedActionMapFromNativeControllerConfig()
    {
        var config = new FakeControllerConfig(
            new Dictionary<string, string>
            {
                ["View_Map"] = "controller_ps4_touchpad"
            },
            new Dictionary<string, string>
            {
                ["mega_view_map"] = "controller_ps4_touchpad"
            });

        var map = SteamControllerInputSelection.CreateNativeGeneratedActionMapForTesting(config);

        Assert.AreEqual("mega_view_map", map["controller_ps4_touchpad"]);
    }

    [TestMethod]
    public void UsesCurrentSts2ControllerSchemeForMappedTargets()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.SetNativeGeneratedActionMapForTesting(
            new Dictionary<string, string>
            {
                ["controller_face_button_west"] = "mega_top_panel",
                ["controller_left_trigger"] = "mega_view_draw_pile",
                ["controller_right_trigger"] = "mega_view_discard_pile",
                ["controller_left_bumper"] = "mega_view_deck_and_tab_left",
                ["controller_right_bumper"] = "mega_view_exhaust_pile_and_tab_right",
                ["controller_select_button"] = "mega_view_map",
                ["ui_controller_touch_pad"] = "mega_view_map",
                ["controller_start_button"] = "mega_pause_and_back",
                ["controller_joystick_press"] = "mega_peek"
            });

        Assert.AreEqual("ui_accept", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_face_button_north", device: 0)));
        Assert.AreEqual("ui_select", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_face_button_south", device: 0)));
        Assert.AreEqual("ui_cancel", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_face_button_east", device: 0)));
        Assert.AreEqual("mega_top_panel", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_face_button_west", device: 0)));
        Assert.AreEqual("mega_view_map", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_select_button", device: 0)));
        Assert.AreEqual("mega_view_map", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("ui_controller_touch_pad", device: 0)));
        Assert.AreEqual("mega_peek", SteamControllerInputSelection.GetMappedTargetAction(
            new FakeInputEventAction("controller_joystick_press", device: 0)));
    }

    [TestMethod]
    public void ConsumesNativeGeneratedActionForSelectedSteamControllerAction()
    {
        var now = new DateTimeOffset(2026, 6, 12, 22, 30, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.SetNativeGeneratedActionMapForTesting(
            new Dictionary<string, string>
            {
                ["controller_face_button_west"] = "mega_top_panel"
            });

        SteamControllerInputSelection.RegisterGeneratedNativeAction(
            new FakeInputEventAction("controller_face_button_west", device: 0),
            now);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction("mega_top_panel", device: 0),
            now.AddMilliseconds(10)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction("mega_top_panel", device: 0),
            now.AddMilliseconds(20)));
    }

    [TestMethod]
    public void NativeGeneratedActionTokenExpiresAndDoesNotMatchOtherActions()
    {
        var now = new DateTimeOffset(2026, 6, 12, 22, 30, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.SetNativeGeneratedActionMapForTesting(
            new Dictionary<string, string>
            {
                ["controller_face_button_west"] = "mega_top_panel"
            });

        SteamControllerInputSelection.RegisterGeneratedNativeAction(
            new FakeInputEventAction("controller_face_button_west", device: 0),
            now);

        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction("mega_view_map", device: 0),
            now.AddMilliseconds(10)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction("mega_top_panel", device: 0),
            now.AddMilliseconds(251)));
    }

    [TestMethod]
    public void FallsBackToObservedXboxTopPanelNativeActionWhenConfigMapIsUnavailable()
    {
        var now = new DateTimeOffset(2026, 6, 12, 22, 30, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedNativeAction(
            new FakeInputEventAction("controller_face_button_west", device: 0),
            now);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction("mega_top_panel", device: 0),
            now.AddMilliseconds(10)));
    }

    [DataTestMethod]
    [DataRow("controller_left_bumper", "mega_view_deck_and_tab_left")]
    [DataRow("controller_right_bumper", "mega_view_exhaust_pile_and_tab_right")]
    [DataRow("controller_start_button", "mega_pause_and_back")]
    [DataRow("controller_select_button", "mega_view_map")]
    [DataRow("ui_controller_touch_pad", "mega_view_map")]
    [DataRow("controller_joystick_press", "mega_peek")]
    public void FallsBackToObservedXboxNativeActionAliasesWhenConfigMapIsUnavailable(
        string sourceAction,
        string nativeAction)
    {
        var now = new DateTimeOffset(2026, 6, 13, 17, 37, 0, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterGeneratedNativeAction(
            new FakeInputEventAction(sourceAction, device: 0),
            now);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedNativeInputEvent(
            new FakeInputEventAction(nativeAction, device: 0),
            now.AddMilliseconds(10)));
    }

    [TestMethod]
    public void ConsumesGeneratedOriginalSteamControllerMotionByShapeForSinkClone()
    {
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.RegisterGeneratedOriginalSteamControllerInput(
            new FakeInputEventJoypadMotion(device: 0, axis: 1));

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeGeneratedOriginalSteamControllerInput(
            new FakeInputEventJoypadMotion(device: 0, axis: 1)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedOriginalSteamControllerInput(
            new FakeInputEventJoypadMotion(device: 0, axis: 1)));
        Assert.IsFalse(SteamControllerInputSelection.TryConsumeGeneratedOriginalSteamControllerInput(
            new FakeInputEventJoypadMotion(device: 0, axis: 0)));
    }

    [TestMethod]
    public void NativeFallbackTrustsGeneratedActionAfterAssignedJoypadButton()
    {
        var now = new DateTimeOffset(2026, 7, 17, 14, 41, 6, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterNativeFallbackSourceInput(
            new FakeInputEventJoypadButton(device: 3),
            new BrokerControllerDeviceAssignment(IsConfigured: true, Device: 3),
            now);

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeNativeFallbackGeneratedInput(
            new FakeInputEventAction("ui_down", device: 0),
            new BrokerControllerDeviceAssignment(IsConfigured: true, Device: 3),
            now.AddMilliseconds(10)));
    }

    [TestMethod]
    public void NativeFallbackTrustsGeneratedActionReleaseAfterAssignedJoypadButtonRelease()
    {
        var now = new DateTimeOffset(2026, 7, 17, 14, 41, 6, TimeSpan.Zero);
        var assignment = new BrokerControllerDeviceAssignment(IsConfigured: true, Device: 3);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterNativeFallbackSourceInput(
            new FakeInputEventJoypadButton(device: 3, pressed: true),
            assignment,
            now);
        SteamControllerInputSelection.RegisterNativeFallbackSourceInput(
            new FakeInputEventJoypadButton(device: 3, pressed: false),
            assignment,
            now.AddMilliseconds(20));

        Assert.IsTrue(SteamControllerInputSelection.TryConsumeNativeFallbackGeneratedInput(
            new FakeInputEventAction("ui_select", device: 0, pressed: true),
            assignment,
            now.AddMilliseconds(30)));
        Assert.IsTrue(SteamControllerInputSelection.TryConsumeNativeFallbackGeneratedInput(
            new FakeInputEventAction("ui_select", device: 0, pressed: false),
            assignment,
            now.AddMilliseconds(40)));
    }

    [TestMethod]
    public void NativeFallbackDoesNotTrustGeneratedActionWithoutAssignedJoypadButton()
    {
        var now = new DateTimeOffset(2026, 7, 17, 14, 41, 6, TimeSpan.Zero);
        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();

        SteamControllerInputSelection.RegisterNativeFallbackSourceInput(
            new FakeInputEventJoypadButton(device: 0),
            new BrokerControllerDeviceAssignment(IsConfigured: true, Device: 3),
            now);

        Assert.IsFalse(SteamControllerInputSelection.TryConsumeNativeFallbackGeneratedInput(
            new FakeInputEventAction("ui_down", device: 0),
            new BrokerControllerDeviceAssignment(IsConfigured: true, Device: 3),
            now.AddMilliseconds(10)));
    }

    [TestMethod]
    public void RefreshSelectedInputStateForFrameRunsSteamInputFrameAndReactivatesCurrentActionSet()
    {
        Steamworks.SteamInput.Reset();
        var strategy = new FakeSteamInputStrategy
        {
            _currentControllerHandle = new Steamworks.InputHandle_t(5733059612895879),
            _currentActionSetHandle = new Steamworks.InputActionSetHandle_t(77)
        };

        Assert.IsTrue(SteamControllerInputSelection.RefreshSelectedInputStateForFrame(strategy));

        Assert.AreEqual(1, Steamworks.SteamInput.RunFrameCalls);
        CollectionAssert.AreEqual(
            new[]
            {
                (new Steamworks.InputHandle_t(5733059612895879), new Steamworks.InputActionSetHandle_t(77))
            },
            Steamworks.SteamInput.ActivatedActionSets.ToArray());
    }

    [TestMethod]
    public void RefreshSelectedInputStateForFrameResolvesControlsActionSetWhenMissing()
    {
        Steamworks.SteamInput.Reset();
        Steamworks.SteamInput.ControlsActionSet = new Steamworks.InputActionSetHandle_t(91);
        var strategy = new FakeSteamInputStrategy
        {
            _currentControllerHandle = new Steamworks.InputHandle_t(10237214364789383)
        };

        Assert.IsTrue(SteamControllerInputSelection.RefreshSelectedInputStateForFrame(strategy));

        Assert.AreEqual(new Steamworks.InputActionSetHandle_t(91), strategy._currentActionSetHandle);
        CollectionAssert.AreEqual(
            new[]
            {
                (new Steamworks.InputHandle_t(10237214364789383), new Steamworks.InputActionSetHandle_t(91))
            },
            Steamworks.SteamInput.ActivatedActionSets.ToArray());
    }

    [TestMethod]
    public void ApplySelectionRunsSteamInputFrameBeforeControllerDiscovery()
    {
        Steamworks.SteamInput.Reset();
        Steamworks.SteamInput.ConnectedControllers.Add(new Steamworks.InputHandle_t(5733059612895879));
        var strategy = new FakeSteamInputStrategy();
        var logs = new List<string>();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.ApplySelection(
            strategy,
            BrokerControllerDeviceAssignment.ForDevice(0),
            controllerClientCount: 4,
            claimScope: Guid.NewGuid().ToString("N"),
            clientIndex: 0,
            logs.Add);

        Assert.AreEqual(1, Steamworks.SteamInput.RunFrameCalls);
        Assert.AreEqual(1, Steamworks.SteamInput.GetConnectedControllersCalls);
        Assert.AreEqual(new Steamworks.InputHandle_t(5733059612895879), strategy._currentControllerHandle);
        Assert.IsTrue(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        StringAssert.Contains(logs.Single(), "selected playerSlot=0");
    }

    [TestMethod]
    public void ApplySelectionFallsBackWhenSteamHandleHasNoBoundActionOrigins()
    {
        Steamworks.SteamInput.Reset();
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        var silentHandle = new Steamworks.InputHandle_t(14740258867636871);
        Steamworks.SteamInput.ConnectedControllers.Add(silentHandle);
        var strategy = new FakeSteamInputStrategy();
        var logs = new List<string>();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.ApplySelection(
            strategy,
            BrokerControllerDeviceAssignment.ForDevice(0),
            controllerClientCount: 4,
            claimScope: Guid.NewGuid().ToString("N"),
            clientIndex: 0,
            logs.Add);

        Assert.IsNull(strategy._currentControllerHandle);
        Assert.IsFalse(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        StringAssert.Contains(logs.Single(), "unusable");
        StringAssert.Contains(logs.Single(), "no bound action origins");
    }

    [TestMethod]
    public void ApplySelectionFallsBackWhileSteamBindingIsStillLoading()
    {
        Steamworks.SteamInput.Reset();
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = false;
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        var loadingHandle = new Steamworks.InputHandle_t(5733614737418887);
        Steamworks.SteamInput.ConnectedControllers.Add(loadingHandle);
        var strategy = new FakeSteamInputStrategy();
        var logs = new List<string>();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.ApplySelection(
            strategy,
            BrokerControllerDeviceAssignment.ForDevice(0),
            controllerClientCount: 4,
            claimScope: Guid.NewGuid().ToString("N"),
            clientIndex: 0,
            logs.Add);

        Assert.IsNull(strategy._currentControllerHandle);
        Assert.IsFalse(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        StringAssert.Contains(logs.Single(), "pending");
        StringAssert.Contains(logs.Single(), "binding configuration is still loading");
    }

    [TestMethod]
    public void ApplySelectionUsesAvailableOriginsWhenBindingRevisionIsUnavailable()
    {
        Steamworks.SteamInput.Reset();
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = false;
        var handle = new Steamworks.InputHandle_t(5733614737418887);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        var strategy = new FakeSteamInputStrategy();
        var logs = new List<string>();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.ApplySelection(
            strategy,
            BrokerControllerDeviceAssignment.ForDevice(0),
            controllerClientCount: 4,
            claimScope: Guid.NewGuid().ToString("N"),
            clientIndex: 0,
            logs.Add);

        Assert.AreEqual(handle, strategy._currentControllerHandle);
        Assert.IsTrue(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        StringAssert.Contains(logs.Single(), "selected playerSlot=0");
    }

    [TestMethod]
    public void ApplySelectionAcceptsUnknownSteamInputTypeWhenActionBindingsAreUsable()
    {
        Steamworks.SteamInput.Reset();
        var unknownHandle = new Steamworks.InputHandle_t(250791071447356);
        Steamworks.SteamInput.ConnectedControllers.Add(unknownHandle);
        Steamworks.SteamInput.InputTypesByHandle[unknownHandle] =
            Steamworks.ESteamInputType.k_ESteamInputType_Unknown;
        var strategy = new FakeSteamInputStrategy();
        var logs = new List<string>();

        SteamControllerInputSelection.ClearGeneratedInputEventsForTesting();
        SteamControllerInputSelection.ApplySelection(
            strategy,
            BrokerControllerDeviceAssignment.ForDevice(0),
            controllerClientCount: 4,
            claimScope: Guid.NewGuid().ToString("N"),
            clientIndex: 0,
            logs.Add);

        Assert.AreEqual(unknownHandle, strategy._currentControllerHandle);
        Assert.IsTrue(SteamControllerInputSelection.IsSelectedControllerActive(
            BrokerControllerDeviceAssignment.ForDevice(0)));
        StringAssert.Contains(logs.Single(), "selected playerSlot=0");
        StringAssert.Contains(logs.Single(), "inputType=k_ESteamInputType_Unknown");
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

    private sealed class FakeInputEventJoypadMotion(int device, int axis)
    {
        public int Device { get; } = device;
        public int Axis { get; } = axis;
    }

    private sealed class FakeInputEventJoypadButton(int device, bool pressed = true)
    {
        public int Device { get; } = device;
        public bool Pressed { get; } = pressed;
    }

    private sealed class FakeControllerConfig(
        Dictionary<string, string> steamInputControllerMap,
        Dictionary<string, string> defaultControllerInputMap)
    {
        public Dictionary<string, string> SteamInputControllerMap { get; } = steamInputControllerMap;
        public Dictionary<string, string> DefaultControllerInputMap { get; } = defaultControllerInputMap;
    }

    private sealed class FakeSteamInputStrategy
    {
        public Steamworks.InputHandle_t? _currentControllerHandle;
        public Steamworks.InputActionSetHandle_t? _currentActionSetHandle;
    }
}

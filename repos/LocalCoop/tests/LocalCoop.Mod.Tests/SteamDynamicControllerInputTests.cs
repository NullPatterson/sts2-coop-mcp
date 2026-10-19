using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class SteamDynamicControllerInputTests
{
    [TestInitialize]
    public void Reset()
    {
        Steamworks.SteamInput.Reset();
        Godot.Input.Parsed.Clear();
        Godot.Input.ReleasedActions.Clear();
        Godot.Input.ConnectedJoypads.Clear();
        Godot.Input.JoyInfo.Clear();
        Godot.InputMap.Actions.Clear();
        Godot.InputMap.Matches.Clear();
    }

    [TestMethod]
    public void EnumeratesOpaqueHandlesAndPreservesSteamOrder()
    {
        var first = new Steamworks.InputHandle_t(900);
        var second = new Steamworks.InputHandle_t(100);
        Steamworks.SteamInput.ConnectedControllers.AddRange([first, second]);
        Steamworks.SteamInput.InputTypesByHandle[first] = Steamworks.ESteamInputType.k_ESteamInputType_XBoxOneController;
        Steamworks.SteamInput.InputTypesByHandle[second] = Steamworks.ESteamInputType.k_ESteamInputType_PS5Controller;
        var collector = new SteamDynamicControllerInput();

        var inventory = collector.ReadInventory(new FakeStrategy(), expectedControllerCount: 2);

        CollectionAssert.AreEqual(new[] { "steam:900", "steam:100" }, inventory.Select(item => item.SourceId).ToArray());
        Assert.IsTrue(inventory.All(item => item.GodotDeviceId is null));
        Assert.AreEqual(2, Steamworks.SteamInput.ActivatedActionSets.Count);
    }

    [TestMethod]
    public void PollsDigitalEdgesPerHandleWithoutInitialReleaseNoise()
    {
        var first = new Steamworks.InputHandle_t(10);
        var second = new Steamworks.InputHandle_t(20);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.AddRange([first, second]);
        Steamworks.SteamInput.DigitalActionData[(first, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        Steamworks.SteamInput.DigitalActionData[(second, action)] = new Steamworks.InputDigitalActionData_t(false, true);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var inventory = collector.ReadInventory(strategy, expectedControllerCount: 2);

        var pressed = collector.Poll(strategy, inventory);
        var unchanged = collector.Poll(strategy, inventory);
        Steamworks.SteamInput.DigitalActionData[(first, action)] = new Steamworks.InputDigitalActionData_t(false, true);
        var released = collector.Poll(strategy, inventory);

        Assert.AreEqual(1, pressed.Count);
        Assert.AreEqual("steam:10", pressed[0].SourceId);
        Assert.IsTrue(pressed[0].Pressed);
        Assert.AreEqual(0, unchanged.Count);
        Assert.AreEqual(1, released.Count);
        Assert.IsFalse(released[0].Pressed);
        Assert.AreEqual(2, released[0].ControllerSequence);
        Assert.AreEqual(4, Steamworks.SteamInput.RunFrameCalls);
        StringAssert.Contains(collector.LastPollSummary, "digitalActive=2/2");
        StringAssert.Contains(collector.LastPollSummary, "pressed=[]");
    }

    [TestMethod]
    public void StandbyProbeWarmsActionsWithoutConsumingFirstCollectorEdge()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var inventory = collector.ReadInventory(strategy, expectedControllerCount: 1);

        var readiness = collector.Probe(strategy, inventory);
        var firstCollectorFrame = collector.Poll(strategy, inventory);

        Assert.IsTrue(readiness.IsReady);
        Assert.AreEqual(1, readiness.ActiveDigitalActionCount);
        Assert.AreEqual(1, firstCollectorFrame.Count);
        Assert.IsTrue(firstCollectorFrame.Single().Pressed);
        Assert.AreEqual(1, firstCollectorFrame.Single().ControllerSequence);
    }

    [TestMethod]
    public void StandbyProbeIgnoresUnassignedControllerThatIsStillLoading()
    {
        var assigned = new Steamworks.InputHandle_t(10);
        var standby = new Steamworks.InputHandle_t(20);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.AddRange([assigned, standby]);
        Steamworks.SteamInput.DigitalActionData[(assigned, action)] = new Steamworks.InputDigitalActionData_t(false, true);
        Steamworks.SteamInput.DigitalActionData[(standby, action)] = new Steamworks.InputDigitalActionData_t(false, false);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var assignedInventory = collector.ReadInventory(strategy, expectedControllerCount: 2)
            .Where(source => source.SourceId == "steam:10")
            .ToArray();

        var readiness = collector.Probe(strategy, assignedInventory);

        Assert.IsTrue(readiness.IsReady);
        Assert.AreEqual(1, readiness.ConnectedControllerCount);
        Assert.AreEqual(1, readiness.ActiveDigitalActionCount);
        Assert.AreEqual(1, readiness.DigitalActionQueryCount);
    }

    [TestMethod]
    public void CollectorReadinessRequiresEveryAssignedSteamController()
    {
        var oneObserved = new ControllerActionReadiness(1, 1, 1, 0, 0);
        var twoObserved = new ControllerActionReadiness(2, 2, 2, 0, 0);

        Assert.IsFalse(DynamicControllerCoordinator.IsActionDataReadyForTesting(oneObserved, requiredSteamControllerCount: 2));
        Assert.IsTrue(DynamicControllerCoordinator.IsActionDataReadyForTesting(twoObserved, requiredSteamControllerCount: 2));
    }

    [TestMethod]
    public void NativeFallbackCannotMaskMissingAssignedSteamController()
    {
        var oneOfTwoSteamControllersObserved = new ControllerActionReadiness(1, 1, 1, 0, 0);

        var mixedReady = DynamicControllerCoordinator.IsActionDataReadyForTesting(
            oneOfTwoSteamControllersObserved,
            requiredSteamControllerCount: 2,
            requiredNativeControllerCount: 1,
            observedNativeControllerCount: 1,
            focused: true);
        var nativeOnlyReady = DynamicControllerCoordinator.IsActionDataReadyForTesting(
            ControllerActionReadiness.Empty,
            requiredSteamControllerCount: 0,
            requiredNativeControllerCount: 1,
            observedNativeControllerCount: 1,
            focused: true);

        Assert.IsFalse(mixedReady);
        Assert.IsTrue(nativeOnlyReady);
    }

    [TestMethod]
    public void LeaseResetMakesFirstCollectorFrameACompleteHeldStateSnapshot()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var inventory = collector.ReadInventory(strategy, expectedControllerCount: 1);
        collector.Poll(strategy, inventory);

        collector.ResetPollingState();
        var snapshot = collector.Poll(strategy, inventory);

        Assert.AreEqual(1, snapshot.Count);
        Assert.IsTrue(snapshot.Single().Pressed);
        Assert.AreEqual(1, snapshot.Single().ControllerSequence);
    }

    [TestMethod]
    public void AppliesAssignedHandleAndInjectsGameTemplate()
    {
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var input = new ControllerInputValue(
            "steam:20",
            1,
            "controller_face_button_south",
            ControllerInputValueKind.Digital,
            Pressed: true);

        var applied = collector.ApplyAssignedHandle(strategy, "20");
        var injected = collector.Inject(strategy, input);

        Assert.IsTrue(applied);
        Assert.AreEqual(new Steamworks.InputHandle_t(20), strategy._currentControllerHandle);
        Assert.AreEqual(1, strategy.UpdateInputMapCalls);
        Assert.IsTrue(injected);
        var parsed = (LocalCoopInputRouterTests.ParsedInputEvent)Godot.Input.Parsed.Single();
        Assert.AreEqual("controller_face_button_south", parsed.Action);
        Assert.IsTrue(parsed.Pressed);
        Assert.IsTrue(SteamControllerInputSelection.IsGeneratedInputEvent(parsed));
    }

    [TestMethod]
    public void BindingRevisionLoadingUsesOriginsAndEventuallyBecomesReady()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = false;
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        var collector = new SteamDynamicControllerInput();
        var strategy = new FakeStrategy(action);

        var loading = collector.ReadInventory(strategy, expectedControllerCount: 1);
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 1;
        var originsReady = collector.ReadInventory(strategy, expectedControllerCount: 1);
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = true;
        var revisionReady = collector.ReadInventory(strategy, expectedControllerCount: 1);

        Assert.AreEqual(ControllerConnectionState.Binding, loading.Single().ConnectionState);
        Assert.AreEqual(ControllerConnectionState.Ready, originsReady.Single().ConnectionState);
        Assert.AreEqual(ControllerConnectionState.Ready, revisionReady.Single().ConnectionState);
    }

    [TestMethod]
    public void ActiveActionDataPromotesBindingControllerAndRoutesItsFirstInput()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = false;
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        var collector = new SteamDynamicControllerInput();
        var strategy = new FakeStrategy(action);
        var inventory = collector.ReadInventory(strategy, expectedControllerCount: 1);

        var input = collector.Poll(strategy, inventory);
        var promoted = collector.ReadInventory(strategy, expectedControllerCount: 1);

        Assert.AreEqual(1, input.Count);
        Assert.IsTrue(input.Single().Pressed);
        Assert.AreEqual(ControllerConnectionState.Ready, promoted.Single().ConnectionState);
    }

    [TestMethod]
    public void ReadySteamControllerDoesNotDowngradeWhenBindingQueryBecomesTemporarilyUnavailable()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = true;
        var collector = new SteamDynamicControllerInput();
        var strategy = new FakeStrategy(action);

        var ready = collector.ReadInventory(strategy, expectedControllerCount: 1);
        Steamworks.SteamInput.DeviceBindingRevisionLoaded = false;
        Steamworks.SteamInput.DefaultDigitalActionOriginCount = 0;
        var temporarilyUnavailable = collector.ReadInventory(strategy, expectedControllerCount: 1);

        Assert.AreEqual(ControllerConnectionState.Ready, ready.Single().ConnectionState);
        Assert.AreEqual(ControllerConnectionState.Ready, temporarilyUnavailable.Single().ConnectionState);
    }

    [TestMethod]
    public void NativeFallbackCorrelatesDocumentedIndicesAndScopesAmbiguousDevicesToCollectorLease()
    {
        var steamHandle = new Steamworks.InputHandle_t(10);
        Steamworks.SteamInput.ConnectedControllers.Add(steamHandle);
        Steamworks.SteamInput.ControllersByGamepadIndex[0] = steamHandle;
        Godot.Input.ConnectedJoypads.AddRange([0, 1, 2]);
        Godot.Input.JoyInfo[0] = new() { ["steam_input_index"] = 0 };
        Godot.Input.JoyInfo[1] = new() { ["serial_number"] = "EXACT-1", ["raw_name"] = "Pad" };
        Godot.Input.JoyInfo[2] = new() { ["raw_name"] = "__XINPUT_DEVICE__" };
        var collector = new SteamDynamicControllerInput();

        var firstLease = collector.ReadInventory(new FakeStrategy(), 3, "client-0:lease1");
        var secondLease = collector.ReadInventory(new FakeStrategy(), 3, "client-1:lease2");

        Assert.AreEqual(3, firstLease.Count);
        Assert.IsFalse(firstLease.Any(source => source.GodotDeviceId == 0));
        Assert.IsTrue(firstLease.Any(source => source.SourceId == "godot:serial:EXACT-1"));
        var firstAmbiguous = firstLease.Single(source => source.GodotDeviceId == 2).SourceId;
        var secondAmbiguous = secondLease.Single(source => source.GodotDeviceId == 2).SourceId;
        Assert.AreNotEqual(firstAmbiguous, secondAmbiguous);
    }

    [TestMethod]
    public void DefersAnonymousNativeFallbackWhileSteamDiscoveryIsStillLoading()
    {
        Godot.Input.ConnectedJoypads.AddRange([0, 1, 2, 3]);
        var collector = new SteamDynamicControllerInput();

        var inventory = collector.ReadInventory(new FakeStrategy(), 4, "client-0:lease1");

        Assert.AreEqual(0, inventory.Count);
    }

    [TestMethod]
    public void CommittedNativeFallbackIsNotEvictedByLateUncorrelatedSteamHandles()
    {
        Godot.Input.ConnectedJoypads.Add(0);
        var collector = new SteamDynamicControllerInput(TimeSpan.Zero);
        var strategy = new FakeStrategy();
        var nativeInventory = collector.ReadInventory(strategy, 1, "client-0:lease1");
        Steamworks.SteamInput.ConnectedControllers.Add(new Steamworks.InputHandle_t(10));

        var combinedInventory = collector.ReadInventory(strategy, 1, "client-0:lease1");

        Assert.AreEqual("godot:collector:client-0:lease1:device:0", nativeInventory.Single().SourceId);
        Assert.IsTrue(combinedInventory.Any(source => source.SourceId == nativeInventory.Single().SourceId));
        Assert.IsTrue(combinedInventory.Any(source => source.SourceId == "steam:10"));
    }

    [TestMethod]
    public void SharedXInputGuidDoesNotMergeIndistinguishableNativeControllers()
    {
        Godot.Input.ConnectedJoypads.AddRange([0, 1]);
        Godot.Input.JoyInfo[0] = new() { ["raw_name"] = "__XINPUT_DEVICE__", ["vendor_id"] = 0, ["product_id"] = 0 };
        Godot.Input.JoyInfo[1] = new() { ["raw_name"] = "__XINPUT_DEVICE__", ["vendor_id"] = 0, ["product_id"] = 0 };
        var collector = new SteamDynamicControllerInput(TimeSpan.Zero);

        var inventory = collector.ReadInventory(new FakeStrategy(), 2, "client-0:lease1");

        Assert.AreEqual(2, inventory.Count);
        Assert.AreEqual(2, inventory.Select(source => source.SourceId).Distinct().Count());
    }

    [TestMethod]
    public void NativeFallbackUsesCurrentGodotInputMapWithoutSteamControllerConfig()
    {
        Godot.Input.ConnectedJoypads.Add(0);
        Godot.InputMap.Actions.Add("controller_face_button_south");
        var inputEvent = new FakeInputEventJoypadButton { Device = 0, Pressed = true };
        Godot.InputMap.Matches[inputEvent] = ["controller_face_button_south"];
        var collector = new SteamDynamicControllerInput(TimeSpan.Zero);
        var strategy = new FakeStrategy();
        var inventory = collector.ReadInventory(strategy, 1, "client-0:lease1");

        var translated = collector.TranslateNativeEvent(strategy, inputEvent, inventory);
        var injected = translated.Count == 1 && collector.Inject(strategy, translated[0]);

        Assert.AreEqual(1, translated.Count);
        Assert.AreEqual("controller_face_button_south", translated[0].ActionId);
        Assert.IsTrue(translated[0].Pressed);
        Assert.IsTrue(injected);
        CollectionAssert.AreEqual(new[] { "controller_face_button_south" }, Godot.Input.ReleasedActions);
        var parsed = (Godot.InputEventAction)Godot.Input.Parsed.Single();
        Assert.AreEqual("controller_face_button_south", parsed.Action);
        Assert.IsTrue(parsed.Pressed);
    }

    [TestMethod]
    public void NativeFallbackEmitsOnlyDigitalEdgesAndSuppressesInitialReleaseNoise()
    {
        Godot.Input.ConnectedJoypads.Add(0);
        Godot.InputMap.Actions.Add("controller_face_button_south");
        var released = new FakeInputEventJoypadButton { Device = 0, Pressed = false };
        var pressed = new FakeInputEventJoypadButton { Device = 0, Pressed = true };
        Godot.InputMap.Matches[released] = ["controller_face_button_south"];
        Godot.InputMap.Matches[pressed] = ["controller_face_button_south"];
        var collector = new SteamDynamicControllerInput(TimeSpan.Zero);
        var strategy = new FakeStrategy();
        var inventory = collector.ReadInventory(strategy, 1, "client-0:lease1");

        var initialRelease = collector.TranslateNativeEvent(strategy, released, inventory);
        var firstPress = collector.TranslateNativeEvent(strategy, pressed, inventory);
        var repeatedPress = collector.TranslateNativeEvent(strategy, pressed, inventory);
        var finalRelease = collector.TranslateNativeEvent(strategy, released, inventory);

        Assert.AreEqual(0, initialRelease.Count);
        Assert.AreEqual(1, firstPress.Count);
        Assert.IsTrue(firstPress[0].Pressed);
        Assert.AreEqual(0, repeatedPress.Count);
        Assert.AreEqual(1, finalRelease.Count);
        Assert.IsFalse(finalRelease[0].Pressed);
        Assert.AreEqual(4, Godot.Input.ReleasedActions.Count);
    }

    [TestMethod]
    public void UsesCurrentGameTemplateActionWithoutPhysicalButtonMapping()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        var strategy = new FakeStrategy(action);
        var collector = new SteamDynamicControllerInput();
        var inventory = collector.ReadInventory(strategy, 1);

        var original = collector.Poll(strategy, inventory).Single();
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(false, true);
        collector.Poll(strategy, inventory);
        ((LocalCoopInputRouterTests.ParsedInputEvent)strategy._inputEvents["steam_action_south"]).Action = "user_rebound_action";
        Steamworks.SteamInput.DigitalActionData[(handle, action)] = new Steamworks.InputDigitalActionData_t(true, true);
        var rebound = collector.Poll(strategy, inventory).Single();

        Assert.AreEqual("controller_face_button_south", original.ActionId);
        Assert.AreEqual("user_rebound_action", rebound.ActionId);
    }

    [TestMethod]
    public void SendsAnalogMovementAndNeutralWhenActionBecomesInactive()
    {
        var handle = new Steamworks.InputHandle_t(10);
        var analog = new Steamworks.InputAnalogActionHandle_t(3);
        Steamworks.SteamInput.ConnectedControllers.Add(handle);
        Steamworks.SteamInput.AnalogActionData[(handle, analog)] = new Steamworks.InputAnalogActionData_t(0.75f, -0.25f, true);
        var strategy = new FakeStrategy { _joystickActionHandle = analog };
        var collector = new SteamDynamicControllerInput();
        var inventory = collector.ReadInventory(strategy, 1);

        var moved = collector.Poll(strategy, inventory).Single();
        Steamworks.SteamInput.AnalogActionData[(handle, analog)] = new Steamworks.InputAnalogActionData_t(0.75f, -0.25f, false);
        var neutral = collector.Poll(strategy, inventory).Single();

        Assert.AreEqual(0.75f, moved.X);
        Assert.AreEqual(-0.25f, moved.Y);
        Assert.AreEqual(0f, neutral.X);
        Assert.AreEqual(0f, neutral.Y);
    }

    [TestMethod]
    public void SteamReflectionFailureFailsClosedAndRetainsNativeFallback()
    {
        Steamworks.SteamInput.ThrowOnGetConnectedControllers = true;
        Godot.Input.ConnectedJoypads.Add(0);
        Godot.Input.JoyInfo[0] = new() { ["serial_number"] = "NATIVE-ONLY" };
        var collector = new SteamDynamicControllerInput();

        var inventory = collector.ReadInventory(new FakeStrategy(), 1, "client-0:lease1");

        Assert.AreEqual(1, inventory.Count);
        Assert.AreEqual("godot:serial:NATIVE-ONLY", inventory.Single().SourceId);
        StringAssert.Contains(collector.LastSteamFailure, "MissingMethodException");
    }

    [TestMethod]
    public void AssignmentLossSynthesizesDigitalReleaseAndNeutralAnalogAxes()
    {
        var action = new Steamworks.InputDigitalActionHandle_t(7);
        var strategy = new FakeStrategy(action)
        {
            _joystickXAxis = new Godot.TestAxisInputEvent(0.8f),
            _joystickYAxis = new Godot.TestAxisInputEvent(-0.6f)
        };
        var collector = new SteamDynamicControllerInput();
        collector.Inject(strategy, new ControllerInputValue(
            "steam:10",
            1,
            "controller_face_button_south",
            ControllerInputValueKind.Digital,
            true));
        Godot.Input.Parsed.Clear();

        collector.ReleaseDeliveredInputs(strategy);

        Assert.AreEqual(3, Godot.Input.Parsed.Count);
        var release = (LocalCoopInputRouterTests.ParsedInputEvent)Godot.Input.Parsed[0];
        Assert.IsFalse(release.Pressed);
        Assert.IsTrue(Godot.Input.Parsed.Skip(1).Cast<Godot.TestAxisInputEvent>().All(input => input.AxisValue == 0));
    }

    [TestMethod]
    public void PhysicalSuppressionClassifiesOnlyRawJoypadEvents()
    {
        Assert.IsTrue(DynamicControllerCoordinator.IsRawJoypadInputForTesting(new FakeInputEventJoypadButton()));
        Assert.IsFalse(DynamicControllerCoordinator.IsRawJoypadInputForTesting(new FakeInputEventAction()));
        Assert.IsFalse(DynamicControllerCoordinator.IsRawJoypadInputForTesting(new FakeMouseButton()));
    }

    [TestMethod]
    public void DynamicConnectionRefreshRunsGameMaintenanceAndPreservesAssignedHandle()
    {
        var strategy = new FakeStrategy
        {
            _currentControllerHandle = new Steamworks.InputHandle_t(42)
        };

        var refreshed = LocalCoop.Mod.Patches.SteamControllerInputSelectionPatches.RefreshDynamicControllerConnections(strategy);

        Assert.IsTrue(refreshed);
        Assert.AreEqual(1, strategy.UpdateControllerConnectionsCalls);
        Assert.AreEqual(new Steamworks.InputHandle_t(42), strategy._currentControllerHandle);
    }

    private sealed class FakeStrategy
    {
        public Steamworks.InputHandle_t? _currentControllerHandle = null;
        public Steamworks.InputActionSetHandle_t? _currentActionSetHandle = null;
        public Steamworks.InputAnalogActionHandle_t? _joystickActionHandle = null;
        public Godot.TestAxisInputEvent? _joystickXAxis = null;
        public Godot.TestAxisInputEvent? _joystickYAxis = null;
        public readonly Dictionary<string, object> _inputEvents = [];
        public readonly Dictionary<string, Steamworks.InputDigitalActionHandle_t> _digitalActionHandleCache = [];
        public int UpdateInputMapCalls { get; private set; }
        public int UpdateControllerConnectionsCalls { get; private set; }

        public FakeStrategy()
        {
        }

        public FakeStrategy(Steamworks.InputDigitalActionHandle_t action)
        {
            _inputEvents["steam_action_south"] = new LocalCoopInputRouterTests.ParsedInputEvent(
                "controller_face_button_south",
                0,
                pressed: false);
            _digitalActionHandleCache["steam_action_south"] = action;
        }

        public void UpdateControllerConfig(Steamworks.ESteamInputType inputType)
        {
        }

        public void UpdateInputMap()
        {
            UpdateInputMapCalls++;
        }

        public void UpdateControllerConnections()
        {
            UpdateControllerConnectionsCalls++;
            _currentControllerHandle = new Steamworks.InputHandle_t(999);
        }

        public Dictionary<string, string> GetDefaultControllerInputMap()
        {
            return [];
        }
    }

    private sealed class FakeInputEventJoypadButton
    {
        public int Device { get; init; }

        public bool Pressed { get; init; }
    }

    private sealed class FakeInputEventAction
    {
    }

    private sealed class FakeMouseButton
    {
    }

}

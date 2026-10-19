using LocalCoop.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Broker.Tests;

[TestClass]
public sealed class ControllerSessionCoordinatorTests
{
    private const string SessionId = "controller-test";
    private static readonly BrokerClientRegistration[] Clients =
    [
        new("client-0", BrokerClientRole.Host, 0),
        new("client-1", BrokerClientRole.Client, 1),
        new("client-2", BrokerClientRole.Client, 2),
        new("client-3", BrokerClientRole.Client, 3)
    ];

    [TestMethod]
    public void AssignsStableSteamInventoryOneToOneWithoutHardwareMatching()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        var inventory = new[]
        {
            Steam("900", "SameController"),
            Steam("100", "SameController"),
            Steam("700", "SameController"),
            Steam("300", "SameController")
        };

        ObserveStableInventory(coordinator, "client-0", inventory, now);

        Assert.AreEqual(4, coordinator.Assignments.Count);
        CollectionAssert.AreEqual(
            new[] { "steam:900", "steam:100", "steam:700", "steam:300" },
            coordinator.Assignments.Select(assignment => assignment.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3 },
            coordinator.Assignments.Select(assignment => assignment.PlayerSlot).ToArray());
    }

    [TestMethod]
    public void AssignsOneToOneAcrossEverySteamEnumerationPermutation()
    {
        var sourceIds = new[] { "1", "2", "3", "4" };
        foreach (var permutation in Permutations(sourceIds))
        {
            var coordinator = new ControllerSessionCoordinator(SessionId);
            var now = DateTimeOffset.UtcNow;
            Elect(coordinator, "client-0", focused: true, now);
            RegisterRemainingClients(coordinator, now);

            ObserveStableInventory(coordinator, "client-0", permutation.Select(handle => Steam(handle)).ToArray(), now);

            CollectionAssert.AreEqual(
                permutation.Select(handle => $"steam:{handle}").ToArray(),
                coordinator.Assignments.Select(assignment => assignment.SourceId).ToArray());
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                coordinator.Assignments.Select(assignment => assignment.PlayerSlot).ToArray());
        }
    }

    [TestMethod]
    public void EnumerationReorderingDoesNotReshuffleConnectedAssignments()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        var original = new[] { Steam("1"), Steam("2"), Steam("3"), Steam("4") };
        ObserveStableInventory(coordinator, "client-0", original, now);
        var revision = coordinator.AssignmentRevision;

        ObserveStableInventory(coordinator, "client-0", original.Reverse().ToArray(), now.AddSeconds(2));

        Assert.AreEqual(revision, coordinator.AssignmentRevision);
        CollectionAssert.AreEqual(
            new[] { "steam:1", "steam:2", "steam:3", "steam:4" },
            coordinator.Assignments.Select(assignment => assignment.SourceId).ToArray());
    }

    [TestMethod]
    public void DisconnectAutomaticallyFillsVacatedSlotWithoutCompactingOthers()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2"), Steam("3"), Steam("4")], now);

        Elect(coordinator, "client-0", focused: true, now.AddSeconds(2));
        ObserveStableInventory(
            coordinator,
            "client-0",
            [Steam("1"), Steam("2"), Steam("5"), Steam("4")],
            now.AddSeconds(2));

        Assert.AreEqual(2, coordinator.Assignments.Single(assignment => assignment.SourceId == "steam:5").PlayerSlot);
        Assert.AreEqual(3, coordinator.Assignments.Single(assignment => assignment.SourceId == "steam:4").PlayerSlot);
    }

    [TestMethod]
    public void ExtraControllerStaysStandbyAndFillsDisconnectedSlot()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(
            coordinator,
            "client-0",
            [Steam("1"), Steam("2"), Steam("3"), Steam("4"), Steam("5")],
            now);

        Assert.IsFalse(coordinator.Assignments.Any(assignment => assignment.SourceId == "steam:5"));
        Elect(coordinator, "client-0", focused: true, now.AddSeconds(1));
        ObserveStableInventory(
            coordinator,
            "client-0",
            [Steam("1"), Steam("3"), Steam("4"), Steam("5")],
            now.AddSeconds(1),
            startingSequence: 3);

        Assert.AreEqual(1, coordinator.Assignments.Single(assignment => assignment.SourceId == "steam:5").PlayerSlot);
        Assert.AreEqual(2, coordinator.Assignments.Single(assignment => assignment.SourceId == "steam:3").PlayerSlot);
    }

    [TestMethod]
    public void LateSteamInventoryStaysStandbyWhenCommittedNativeAssignmentsRemainConnected()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        var native = Enumerable.Range(0, 4).Select(Godot).ToArray();
        ObserveStableInventory(coordinator, "client-0", native, now);
        var original = coordinator.Assignments.ToArray();

        ObserveStableInventory(
            coordinator,
            "client-0",
            [Steam("1"), Steam("2"), Steam("3"), Steam("4"), .. native],
            now.AddSeconds(2),
            startingSequence: 3);

        CollectionAssert.AreEqual(original, coordinator.Assignments.ToArray());
    }

    [TestMethod]
    public void FewerAndLateControllersFillLowestVacantSlotsWithoutReshuffling()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2")], now);
        var existing = coordinator.Assignments.ToArray();

        Elect(coordinator, "client-0", focused: true, now.AddSeconds(1));
        ObserveStableInventory(
            coordinator,
            "client-0",
            [Steam("1"), Steam("2"), Steam("3")],
            now.AddSeconds(1),
            startingSequence: 3);

        CollectionAssert.AreEqual(existing, coordinator.Assignments.Take(2).ToArray());
        Assert.AreEqual(2, coordinator.Assignments.Single(assignment => assignment.SourceId == "steam:3").PlayerSlot);
    }

    [TestMethod]
    public void CollectorWaitsForLogoReadinessAndSupportsSkippedLogoFallbackStatus()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        var status = new CollectorStatusMessage(0, true, true, false, true, 1);
        coordinator.Handle(Envelope("client-0", ControllerControlMessageTypes.CollectorStatus, status), Clients, now);

        Assert.IsNull(coordinator.CollectorClientId);

        coordinator.Handle(
            Envelope(
                "client-0",
                ControllerControlMessageTypes.CollectorStatus,
                status with { LogoReady = true }),
            Clients,
            now.AddMilliseconds(1));

        Assert.AreEqual("client-0", coordinator.CollectorClientId);
    }

    [TestMethod]
    public void FocusedCollectorFailoverChangesLeaseButPreservesAssignments()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2")], now);
        var assignments = coordinator.Assignments.ToArray();
        var lease = coordinator.LeaseGeneration;

        Elect(coordinator, "client-0", focused: false, now.AddSeconds(1));
        Elect(coordinator, "client-1", focused: true, now.AddSeconds(1));
        Assert.AreEqual("client-0", coordinator.CollectorClientId);

        Elect(coordinator, "client-1", focused: true, now.AddMilliseconds(1201));

        Assert.AreEqual("client-1", coordinator.CollectorClientId);
        Assert.IsTrue(coordinator.LeaseGeneration > lease);
        CollectionAssert.AreEqual(assignments, coordinator.Assignments.ToArray());
    }

    [TestMethod]
    public void KeepsCurrentCollectorWhileNoEligibleWindowIsFocused()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now, controllerClientCount: 2);
        Elect(coordinator, "client-1", focused: false, now, controllerClientCount: 2);
        var lease = coordinator.LeaseGeneration;

        Elect(coordinator, "client-0", focused: false, now.AddMilliseconds(100), controllerClientCount: 2);
        Elect(coordinator, "client-1", focused: false, now.AddMilliseconds(200), controllerClientCount: 2);

        Assert.AreEqual("client-0", coordinator.CollectorClientId);
        Assert.AreEqual(lease, coordinator.LeaseGeneration);
    }

    [TestMethod]
    public void HeartbeatExpiryElectsEligibleFocusedCollectorWithoutReshufflingAssignments()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2")], now);
        var assignments = coordinator.Assignments.ToArray();

        Elect(coordinator, "client-1", focused: true, now.AddSeconds(2));

        Assert.AreEqual("client-1", coordinator.CollectorClientId);
        CollectionAssert.AreEqual(assignments, coordinator.Assignments.ToArray());
    }

    [TestMethod]
    public void BindingStateTransitionUpdatesAssignmentWithoutChangingSlot()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        var binding = Steam("1") with { ConnectionState = ControllerConnectionState.Binding };
        ObserveStableInventory(coordinator, "client-0", [binding], now);
        var slot = coordinator.Assignments.Single().PlayerSlot;
        var revision = coordinator.AssignmentRevision;

        Elect(coordinator, "client-0", focused: true, now.AddSeconds(1));
        ObserveStableInventory(coordinator, "client-0", [Steam("1")], now.AddSeconds(1), startingSequence: 3);

        Assert.AreEqual(slot, coordinator.Assignments.Single().PlayerSlot);
        Assert.AreEqual(ControllerConnectionState.Ready, coordinator.Assignments.Single().ConnectionState);
        Assert.AreEqual(revision, coordinator.AssignmentRevision);
    }

    [TestMethod]
    public void StartupElectionWaitsForExpectedClientsAndIgnoresTransientFocusOrder()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;

        Elect(coordinator, "client-1", focused: true, now);
        Elect(coordinator, "client-2", focused: false, now);
        Elect(coordinator, "client-3", focused: false, now);
        Assert.IsNull(coordinator.CollectorClientId);

        Elect(coordinator, "client-0", focused: false, now);

        Assert.AreEqual("client-0", coordinator.CollectorClientId);
        Assert.AreEqual(1, coordinator.LeaseGeneration);
    }

    [TestMethod]
    public void FocusedCandidateMustBeActionReadyBeforeMakeBeforeBreakHandoff()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        var lease = coordinator.LeaseGeneration;

        Elect(coordinator, "client-0", focused: false, now.AddSeconds(1));
        Elect(coordinator, "client-1", focused: true, now.AddSeconds(1), actionDataReady: false);
        Elect(coordinator, "client-1", focused: true, now.AddSeconds(2), actionDataReady: false);

        Assert.AreEqual("client-0", coordinator.CollectorClientId);
        Assert.AreEqual(lease, coordinator.LeaseGeneration);

        Elect(coordinator, "client-1", focused: true, now.AddMilliseconds(2100), actionDataReady: true);

        Assert.AreEqual("client-1", coordinator.CollectorClientId);
        Assert.IsTrue(coordinator.LeaseGeneration > lease);
    }

    [TestMethod]
    public void UnchangedStatusHeartbeatDoesNotRebroadcastLeaseOrAssignments()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);

        var routes = coordinator.Handle(
            Envelope(
                "client-0",
                ControllerControlMessageTypes.CollectorStatus,
                ReadyStatus(0, focused: true)),
            Clients,
            now.AddMilliseconds(100));

        Assert.AreEqual(0, routes.Count);
    }

    [TestMethod]
    public void ReadyAssignmentDoesNotDowngradeWhenBindingQueryBecomesTemporarilyUnavailable()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1")], now);
        var slot = coordinator.Assignments.Single().PlayerSlot;

        Elect(coordinator, "client-0", focused: true, now.AddSeconds(1));
        var binding = Steam("1") with { ConnectionState = ControllerConnectionState.Binding };
        ObserveStableInventory(coordinator, "client-0", [binding], now.AddSeconds(1), startingSequence: 3);

        Assert.AreEqual(slot, coordinator.Assignments.Single().PlayerSlot);
        Assert.AreEqual(ControllerConnectionState.Ready, coordinator.Assignments.Single().ConnectionState);
    }

    [TestMethod]
    public void AssignsOnlyToControllerEnabledPlayerSlots()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now, controllerClientCount: 2);
        Elect(coordinator, "client-1", focused: false, now, controllerEnabled: false, controllerClientCount: 2);
        Elect(coordinator, "client-2", focused: false, now, controllerClientCount: 2);
        Elect(coordinator, "client-3", focused: false, now, controllerEnabled: false, controllerClientCount: 2);

        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2")], now);

        CollectionAssert.AreEqual(new[] { 0, 2 }, coordinator.Assignments.Select(assignment => assignment.PlayerSlot).ToArray());
        CollectionAssert.AreEqual(new[] { "client-0", "client-2" }, coordinator.Assignments.Select(assignment => assignment.TargetClientId).ToArray());
    }

    [TestMethod]
    public void RejectsStaleLeaseAndDuplicateControllerSequences()
    {
        var logs = new List<string>();
        var coordinator = new ControllerSessionCoordinator(SessionId, logs.Add);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1")], now);

        var stale = InputEnvelope(
            "client-0",
            coordinator.LeaseGeneration - 1,
            coordinator.AssignmentRevision,
            new ControllerInputValue("steam:1", 1, "controller_face_button_south", ControllerInputValueKind.Digital, true));
        Assert.AreEqual(0, coordinator.Handle(stale, Clients, now.AddSeconds(1)).Count);

        var current = InputEnvelope(
            "client-0",
            coordinator.LeaseGeneration,
            coordinator.AssignmentRevision,
            new ControllerInputValue("steam:1", 1, "controller_face_button_south", ControllerInputValueKind.Digital, true));
        var routed = coordinator.Handle(current, Clients, now.AddSeconds(1));
        Assert.AreEqual(1, routed.Count);
        var routedBatch = ControllerControlMessageSerializer.Deserialize<ControllerInputBatchMessage>(routed.Single().Envelope);
        Assert.AreEqual(0, routedBatch.Inputs.Single().TargetClientIndex);
        Assert.AreEqual(0, coordinator.Handle(current, Clients, now.AddSeconds(1)).Count);
        Assert.IsTrue(logs.Any(log => log.Contains("non-monotonic", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RoutesDeterministicFourControllerTraceToExactlyOneClientEach()
    {
        var coordinator = new ControllerSessionCoordinator(SessionId);
        var now = DateTimeOffset.UtcNow;
        Elect(coordinator, "client-0", focused: true, now);
        RegisterRemainingClients(coordinator, now);
        ObserveStableInventory(coordinator, "client-0", [Steam("1"), Steam("2"), Steam("3"), Steam("4")], now);

        var routes = Enumerable.Range(1, 4)
            .SelectMany(index => coordinator.Handle(
                InputEnvelope(
                    "client-0",
                    coordinator.LeaseGeneration,
                    coordinator.AssignmentRevision,
                    new ControllerInputValue(
                        $"steam:{index}",
                        1,
                        $"action-{index}",
                        ControllerInputValueKind.Digital,
                        true)),
                Clients,
                now.AddSeconds(1)))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "client-0", "client-1", "client-2", "client-3" },
            routes.Select(route => route.TargetClientId).ToArray());
        CollectionAssert.AreEqual(
            new int?[] { 0, 1, 2, 3 },
            routes.Select(route => ControllerControlMessageSerializer
                .Deserialize<ControllerInputBatchMessage>(route.Envelope)
                .Inputs.Single().TargetClientIndex)
                .ToArray());
    }

    private static void Elect(
        ControllerSessionCoordinator coordinator,
        string clientId,
        bool focused,
        DateTimeOffset now,
        bool controllerEnabled = true,
        int controllerClientCount = 4,
        bool actionDataReady = true)
    {
        var client = Clients.Single(client => client.ClientId == clientId);
        var status = new CollectorStatusMessage(
            client.ClientIndex,
            focused,
            true,
            true,
            controllerEnabled,
            controllerClientCount,
            ActionDataReady: actionDataReady,
            ConnectedControllerCount: controllerClientCount,
            ActiveDigitalActionCount: controllerClientCount * 15,
            DigitalActionQueryCount: controllerClientCount * 15,
            ActiveAnalogActionCount: controllerClientCount,
            AnalogActionQueryCount: controllerClientCount);
        coordinator.Handle(Envelope(clientId, ControllerControlMessageTypes.CollectorStatus, status), Clients, now);
    }

    private static CollectorStatusMessage ReadyStatus(int clientIndex, bool focused)
    {
        return new CollectorStatusMessage(
            clientIndex,
            focused,
            SteamReady: true,
            LogoReady: true,
            ControllerEnabled: true,
            ControllerClientCount: 4,
            ActionDataReady: true,
            ConnectedControllerCount: 4,
            ActiveDigitalActionCount: 60,
            DigitalActionQueryCount: 60,
            ActiveAnalogActionCount: 4,
            AnalogActionQueryCount: 4);
    }

    private static void RegisterRemainingClients(ControllerSessionCoordinator coordinator, DateTimeOffset now)
    {
        foreach (var client in Clients.Skip(1))
        {
            Elect(coordinator, client.ClientId, focused: false, now);
        }
    }

    private static void ObserveStableInventory(
        ControllerSessionCoordinator coordinator,
        string clientId,
        IReadOnlyList<ControllerSourceDescriptor> inventory,
        DateTimeOffset now,
        long startingSequence = 1)
    {
        var first = new ControllerInventorySnapshotMessage(coordinator.LeaseGeneration, startingSequence, inventory);
        var second = first with { InventorySequence = startingSequence + 1 };
        coordinator.Handle(Envelope(clientId, ControllerControlMessageTypes.InventorySnapshot, first), Clients, now);
        coordinator.Handle(Envelope(clientId, ControllerControlMessageTypes.InventorySnapshot, second), Clients, now.AddMilliseconds(501));
    }

    private static ControllerSourceDescriptor Steam(string handle, string type = "Xbox")
    {
        return new ControllerSourceDescriptor($"steam:{handle}", ControllerSourceKind.SteamInput, handle, type);
    }

    private static ControllerSourceDescriptor Godot(int device)
    {
        return new ControllerSourceDescriptor(
            $"godot:collector:client-0:lease1:device:{device}",
            ControllerSourceKind.Godot,
            SteamHandle: null,
            ControllerType: "Godot",
            GodotDeviceId: device);
    }

    private static BrokerEnvelope InputEnvelope(
        string clientId,
        long lease,
        long revision,
        params ControllerInputValue[] inputs)
    {
        return Envelope(
            clientId,
            ControllerControlMessageTypes.InputBatch,
            new ControllerInputBatchMessage(lease, revision, inputs));
    }

    private static BrokerEnvelope Envelope<T>(string clientId, string messageType, T message)
    {
        return new BrokerEnvelope(
            SessionId,
            clientId,
            null,
            messageType,
            ControllerControlMessageSerializer.Serialize(message),
            1);
    }

    private static IEnumerable<string[]> Permutations(IReadOnlyList<string> values)
    {
        if (values.Count == 1)
        {
            yield return [values[0]];
            yield break;
        }

        for (var index = 0; index < values.Count; index++)
        {
            var remaining = values.Where((_, candidateIndex) => candidateIndex != index).ToArray();
            foreach (var suffix in Permutations(remaining))
            {
                yield return [values[index], .. suffix];
            }
        }
    }
}

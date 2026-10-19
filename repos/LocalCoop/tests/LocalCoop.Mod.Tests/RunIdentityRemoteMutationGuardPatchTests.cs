using System.Reflection;
using LocalCoop.Mod.Patches;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class RunIdentityRemoteMutationGuardPatchTests
{
    private object? _rememberedOwner;

    [TestCleanup]
    public void Cleanup()
    {
        _rememberedOwner = null;
        RunIdentityAlignment.ClearRememberedBrokerRunForTesting();
    }

    [TestMethod]
    public void TargetMethodResolvesNativeEventChoiceMutationBoundary()
    {
        var target = RunIdentityRemoteMutationGuardPatch.TargetMethodsForTesting.Single(method =>
            string.Equals(method.Name, "ChooseOptionForEvent", StringComparison.Ordinal));

        Assert.IsNotNull(target);
        Assert.AreEqual("ChooseOptionForEvent", target.Name);
        Assert.AreEqual(
            "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            target.DeclaringType?.FullName);
        Assert.AreEqual(typeof(void), ((MethodInfo)target).ReturnType);
    }

    [TestMethod]
    public void TargetMethodsResolveNativeRelicObtainMutationBoundary()
    {
        var targets = RunIdentityRemoteMutationGuardPatch.TargetMethodsForTesting
            .Where(method => string.Equals(
                method.DeclaringType?.FullName,
                "MegaCrit.Sts2.Core.Commands.RelicCmd",
                StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(1, targets.Length, DescribeNativeRelicObtainMethods());
        Assert.AreEqual("Obtain", targets[0].Name);
        CollectionAssert.AreEqual(
            new[]
            {
                "MegaCrit.Sts2.Core.Models.RelicModel",
                "MegaCrit.Sts2.Core.Entities.Players.Player",
                "System.Int32"
            },
            targets[0].GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray());
        var returnType = ((MethodInfo)targets[0]).ReturnType;
        Assert.IsTrue(typeof(Task).IsAssignableFrom(returnType));
        Assert.AreEqual("MegaCrit.Sts2.Core.Models.RelicModel", returnType.GetGenericArguments().Single().FullName);
    }

    [TestMethod]
    public void SuppressedTaskMutationCompletesOriginalTaskShape()
    {
        var target = RunIdentityRemoteMutationGuardPatch.TargetMethodsForTesting.Single(method =>
            string.Equals(method.DeclaringType?.FullName, "MegaCrit.Sts2.Core.Commands.RelicCmd", StringComparison.Ordinal));

        var result = RunIdentityRemoteMutationTaskGuardPatch.SuppressedTaskResultForTesting(target);

        Assert.IsNotNull(result);
        Assert.AreEqual(TaskStatus.RanToCompletion, result!.Status);
    }

    [TestMethod]
    public void SuppressesDirectRemoteEventChoiceMutationOutsideBrokerHandler()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);

            var allowed = RunIdentityRemoteMutationGuard.ShouldAllowNativeMutationForTesting(
                null,
                [new FakePlayer(BrokerPlayerId.ForClientIndex(1)), 0]);

            Assert.IsFalse(allowed);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void AllowsRemoteEventChoiceMutationDuringBrokerHandler()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);
            using var _ = BrokerNetGameService.EnterNativeMessageHandlerDispatchForTesting();

            var allowed = RunIdentityRemoteMutationGuard.ShouldAllowNativeMutationForTesting(
                null,
                [new FakePlayer(BrokerPlayerId.ForClientIndex(1)), 0]);

            Assert.IsTrue(allowed);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void AllowsDirectLocalEventChoiceMutationOutsideBrokerHandler()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);

            var allowed = RunIdentityRemoteMutationGuard.ShouldAllowNativeMutationForTesting(
                null,
                [new FakePlayer(BrokerPlayerId.ForClientIndex(0)), 0]);

            Assert.IsTrue(allowed);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    [TestMethod]
    public void SuppressesDirectRemoteRelicObtainOutsideBrokerHandler()
    {
        var previousNetId = LocalContext.NetId;
        using var service = new BrokerNetGameService(
            new BrokerBackedNetService("local-test", "client-0", 0, new CapturingTransport()),
            NetGameType.Host);

        try
        {
            RememberBrokerRun(service);

            var allowed = RunIdentityRemoteMutationGuard.ShouldAllowNativeMutationForTesting(
                null,
                [new object(), new FakePlayer(BrokerPlayerId.ForClientIndex(1)), -1]);

            Assert.IsFalse(allowed);
        }
        finally
        {
            LocalContext.NetId = previousNetId;
        }
    }

    private void RememberBrokerRun(BrokerNetGameService service)
    {
        _rememberedOwner = new FakeRunManagerOwner(service)
        {
            EventSynchronizer = new FakeLocalPlayerSynchronizer(BrokerPlayerId.ForClientIndex(1))
        };

        Assert.IsTrue(RunIdentityAlignment.AlignBrokerRun(_rememberedOwner));
    }

    private static string DescribeNativeRelicObtainMethods()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    return exception.Types.Where(type => type is not null).Cast<Type>();
                }
            })
            .Single(type => string.Equals(type.FullName, "MegaCrit.Sts2.Core.Commands.RelicCmd", StringComparison.Ordinal));

        return string.Join(
            Environment.NewLine,
            type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(method.Name, "Obtain", StringComparison.Ordinal))
                .Select(method =>
                    $"{method.ReturnType.FullName} {method.Name}({string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.FullName} {parameter.Name}"))}) generic={method.IsGenericMethodDefinition} containsGeneric={method.ContainsGenericParameters}"));
    }

    private sealed class FakeRunManagerOwner(BrokerNetGameService netService)
    {
        public BrokerNetGameService NetService { get; } = netService;

        public required FakeLocalPlayerSynchronizer EventSynchronizer { get; init; }
    }

    private sealed class FakeLocalPlayerSynchronizer(ulong localPlayerId)
    {
        private ulong _localPlayerId = localPlayerId;

        public ulong LocalPlayerId => _localPlayerId;
    }

    private sealed class FakePlayer(ulong netId)
    {
        public ulong NetId { get; } = netId;
    }

    private sealed class CapturingTransport : IBrokerEnvelopeTransport
    {
        public Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrokerEnvelope?> ReceiveEnvelopeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<BrokerEnvelope?>(null);
        }
    }
}

using LocalCoop.Mod.Patches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class NetServiceDiagnosticsPatchesTests
{
    [TestMethod]
    public void ShouldPatchMethodRejectsGenericSendMessageDefinitions()
    {
        var method = typeof(FakeNetClientGameService)
            .GetMethod(nameof(FakeNetClientGameService.SendMessage))!;

        Assert.IsFalse(NetServiceDiagnosticsPatches.ShouldPatchMethod(method));
    }

    [TestMethod]
    public void ShouldPatchMethodAcceptsNonGenericRegisterMessageHandler()
    {
        var method = typeof(FakeNetClientGameService)
            .GetMethod(nameof(FakeNetClientGameService.RegisterMessageHandler))!;

        Assert.IsTrue(NetServiceDiagnosticsPatches.ShouldPatchMethod(method));
    }

    [TestMethod]
    public void ShouldPatchMethodRejectsAbstractTransportMethods()
    {
        var method = typeof(FakeAbstractNetTransport)
            .GetMethod(nameof(FakeAbstractNetTransport.SendMessageToHost))!;

        Assert.IsFalse(NetServiceDiagnosticsPatches.ShouldPatchMethod(method));
    }

    [TestMethod]
    public void ShouldInspectTypeOnlyAcceptsSts2NetOrSteamTypes()
    {
        Assert.IsTrue(NetServiceDiagnosticsPatches.ShouldInspectType(typeof(MegaCrit.Sts2.Core.Multiplayer.FakeSteamTransport)));
        Assert.IsFalse(NetServiceDiagnosticsPatches.ShouldInspectType(typeof(System.Net.WebRequest)));
        Assert.IsFalse(NetServiceDiagnosticsPatches.ShouldInspectType(typeof(FakeNetClientGameService)));
    }

    private sealed class FakeNetClientGameService
    {
        public void SendMessage<T>(T message, ulong playerId)
        {
        }

        public void RegisterMessageHandler(Type messageType, Action<object> handler)
        {
        }
    }

    private abstract class FakeAbstractNetTransport
    {
        public abstract void SendMessageToHost(byte[] bytes, int length);
    }
}

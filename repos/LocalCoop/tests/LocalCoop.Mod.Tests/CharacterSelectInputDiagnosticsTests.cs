using LocalCoop.Mod.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalCoop.Mod.Tests;

[TestClass]
public sealed class CharacterSelectInputDiagnosticsTests
{
    [TestMethod]
    public void FormatEventIncludesSelectedCharacterAndInputAction()
    {
        var screen = new FakeScreenState
        {
            _selectedButton = new FakeButtonState
            {
                Character = new FakeCharacter("CHARACTER.NECROBINDER")
            }
        };

        var line = CharacterSelectInputDiagnostics.FormatEvent(
            "NCharacterSelectScreen",
            "_Input",
            screen,
            [new FakeInputEventAction("ui_accept", pressed: true, device: 1)]);

        StringAssert.Contains(line, "NCharacterSelectScreen._Input");
        StringAssert.Contains(line, "selectedCharacter=CHARACTER.NECROBINDER");
        StringAssert.Contains(line, "input=FakeInputEventAction");
        StringAssert.Contains(line, "action=ui_accept");
        StringAssert.Contains(line, "pressed=True");
        StringAssert.Contains(line, "device=1");
    }

    [TestMethod]
    public void FormatEventIncludesPressedButtonCharacter()
    {
        var line = CharacterSelectInputDiagnostics.FormatEvent(
            "NCharacterSelectButton",
            "OnPress",
            new FakeButtonState { Character = new FakeCharacter("CHARACTER.IRONCLAD") },
            []);

        StringAssert.Contains(line, "NCharacterSelectButton.OnPress");
        StringAssert.Contains(line, "buttonCharacter=CHARACTER.IRONCLAD");
    }

    [TestMethod]
    public void FormatEventHandlesMissingState()
    {
        var line = CharacterSelectInputDiagnostics.FormatEvent(
            "NCharacterSelectScreen",
            "OnEmbarkPressed",
            new object(),
            [new object()]);

        StringAssert.Contains(line, "NCharacterSelectScreen.OnEmbarkPressed");
        StringAssert.Contains(line, "selectedCharacter=<unknown>");
    }

    private sealed class FakeScreenState
    {
        public FakeButtonState? _selectedButton;
    }

    private sealed class FakeButtonState
    {
        public FakeCharacter? Character { get; init; }
    }

    private sealed class FakeCharacter(string id)
    {
        public string Id { get; } = id;
    }

    private sealed class FakeInputEventAction(string action, bool pressed, int device)
    {
        public string Action { get; } = action;
        public bool Pressed { get; } = pressed;
        public int Device { get; } = device;
    }
}

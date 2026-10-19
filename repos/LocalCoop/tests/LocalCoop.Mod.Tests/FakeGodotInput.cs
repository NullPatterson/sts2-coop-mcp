namespace Godot;

public static class Input
{
    public static List<object> Parsed { get; } = [];
    public static List<string> ReleasedActions { get; } = [];
    public static List<int> ConnectedJoypads { get; } = [];
    public static Dictionary<int, Dictionary<string, object>> JoyInfo { get; } = [];

    public static List<int> GetConnectedJoypads()
    {
        return ConnectedJoypads;
    }

    public static Dictionary<string, object> GetJoyInfo(int device)
    {
        return JoyInfo.GetValueOrDefault(device) ?? [];
    }

    public static void ParseInputEvent(LocalCoop.Mod.Tests.LocalCoopInputRouterTests.ParsedInputEvent inputEvent)
    {
        Parsed.Add(inputEvent);
    }

    public static void ParseInputEvent(TestAxisInputEvent inputEvent)
    {
        Parsed.Add(inputEvent);
    }

    public static void ParseInputEvent(InputEventAction inputEvent)
    {
        Parsed.Add(inputEvent);
    }

    public static void ActionRelease(string action)
    {
        ReleasedActions.Add(action);
    }
}

public static class InputMap
{
    public static List<string> Actions { get; } = [];
    public static Dictionary<object, HashSet<string>> Matches { get; } = [];

    public static List<string> GetActions()
    {
        return Actions;
    }

    public static bool EventIsAction(object inputEvent, string action, bool exactMatch = false)
    {
        return Matches.TryGetValue(inputEvent, out var actions) && actions.Contains(action);
    }
}

public sealed class InputEventAction
{
    public string Action { get; set; } = string.Empty;
    public bool Pressed { get; set; }
    public float Strength { get; set; }

    public InputEventAction Duplicate()
    {
        return new InputEventAction { Action = Action, Pressed = Pressed, Strength = Strength };
    }

    public bool IsActionPressed(string action, bool allowEcho = false, bool exactMatch = false)
    {
        return Pressed && string.Equals(Action, action, StringComparison.Ordinal);
    }

    public bool IsActionReleased(string action, bool exactMatch = false)
    {
        return !Pressed && string.Equals(Action, action, StringComparison.Ordinal);
    }
}

public sealed class TestAxisInputEvent(float axisValue)
{
    public float AxisValue { get; set; } = axisValue;

    public TestAxisInputEvent Duplicate()
    {
        return new TestAxisInputEvent(AxisValue);
    }
}

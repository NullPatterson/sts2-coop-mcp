namespace Steamworks;

public static class Constants
{
    public const int STEAM_INPUT_MAX_COUNT = 16;
    public const int STEAM_INPUT_MAX_ORIGINS = 8;
}

public readonly record struct InputHandle_t(ulong Value)
{
    public override string ToString()
    {
        return Value.ToString();
    }
}

public readonly record struct InputActionSetHandle_t(ulong Value)
{
    public override string ToString()
    {
        return Value.ToString();
    }
}

public readonly record struct InputDigitalActionHandle_t(ulong Value)
{
    public override string ToString()
    {
        return Value.ToString();
    }
}

public readonly record struct InputAnalogActionHandle_t(ulong Value)
{
    public override string ToString()
    {
        return Value.ToString();
    }
}

public readonly record struct InputDigitalActionData_t
{
    public readonly byte bState;
    public readonly byte bActive;

    public InputDigitalActionData_t(bool state, bool active)
    {
        bState = state ? (byte)1 : (byte)0;
        bActive = active ? (byte)1 : (byte)0;
    }
}

public readonly record struct InputAnalogActionData_t
{
    public readonly float x;
    public readonly float y;
    public readonly byte bActive;

    public InputAnalogActionData_t(float x, float y, bool active)
    {
        this.x = x;
        this.y = y;
        bActive = active ? (byte)1 : (byte)0;
    }
}

public enum EInputActionOrigin
{
    k_EInputActionOrigin_None,
    k_EInputActionOrigin_XBoxOne_A
}

public enum ESteamInputType
{
    k_ESteamInputType_Unknown,
    k_ESteamInputType_XBoxOneController,
    k_ESteamInputType_PS5Controller
}

public static class SteamInput
{
    public static int RunFrameCalls { get; private set; }
    public static List<(InputHandle_t Handle, InputActionSetHandle_t ActionSet)> ActivatedActionSets { get; } = [];
    public static List<InputHandle_t> ConnectedControllers { get; } = [];
    public static Dictionary<int, InputHandle_t> ControllersByGamepadIndex { get; } = [];
    public static Dictionary<string, InputDigitalActionHandle_t> DigitalActionHandles { get; } = [];
    public static Dictionary<InputHandle_t, ESteamInputType> InputTypesByHandle { get; } = [];
    public static Dictionary<(InputHandle_t Handle, InputDigitalActionHandle_t Action), InputDigitalActionData_t> DigitalActionData { get; } = [];
    public static Dictionary<(InputHandle_t Handle, InputAnalogActionHandle_t Action), InputAnalogActionData_t> AnalogActionData { get; } = [];
    public static int GetConnectedControllersCalls { get; private set; }
    public static InputActionSetHandle_t ControlsActionSet { get; set; } = new(1);
    public static int DefaultDigitalActionOriginCount { get; set; } = 1;
    public static bool DeviceBindingRevisionLoaded { get; set; } = true;
    public static bool ThrowOnGetConnectedControllers { get; set; }

    public static void Reset()
    {
        RunFrameCalls = 0;
        GetConnectedControllersCalls = 0;
        ActivatedActionSets.Clear();
        ConnectedControllers.Clear();
        ControllersByGamepadIndex.Clear();
        DigitalActionHandles.Clear();
        InputTypesByHandle.Clear();
        DigitalActionData.Clear();
        AnalogActionData.Clear();
        ControlsActionSet = new InputActionSetHandle_t(1);
        DefaultDigitalActionOriginCount = 1;
        DeviceBindingRevisionLoaded = true;
        ThrowOnGetConnectedControllers = false;
    }

    public static void RunFrame(bool bReservedValue = true)
    {
        RunFrameCalls++;
    }

    public static InputActionSetHandle_t GetActionSetHandle(string actionSetName)
    {
        return string.Equals(actionSetName, "Controls", StringComparison.Ordinal)
            ? ControlsActionSet
            : default;
    }

    public static void ActivateActionSet(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle)
    {
        ActivatedActionSets.Add((inputHandle, actionSetHandle));
    }

    public static InputDigitalActionHandle_t GetDigitalActionHandle(string actionName)
    {
        if (!DigitalActionHandles.TryGetValue(actionName, out var handle))
        {
            handle = new InputDigitalActionHandle_t((ulong)DigitalActionHandles.Count + 1);
            DigitalActionHandles[actionName] = handle;
        }

        return handle;
    }

    public static int GetDigitalActionOrigins(
        InputHandle_t inputHandle,
        InputActionSetHandle_t actionSetHandle,
        InputDigitalActionHandle_t digitalActionHandle,
        EInputActionOrigin[] origins)
    {
        if (origins.Length != Constants.STEAM_INPUT_MAX_ORIGINS)
        {
            throw new ArgumentException("origins must match STEAM_INPUT_MAX_ORIGINS", nameof(origins));
        }

        var count = Math.Clamp(DefaultDigitalActionOriginCount, 0, origins.Length);
        for (var index = 0; index < count; index++)
        {
            origins[index] = EInputActionOrigin.k_EInputActionOrigin_XBoxOne_A;
        }

        return count;
    }

    public static bool GetDeviceBindingRevision(
        InputHandle_t inputHandle,
        out int major,
        out int minor)
    {
        major = 1;
        minor = 0;
        return DeviceBindingRevisionLoaded;
    }

    public static int GetConnectedControllers(InputHandle_t[] handles)
    {
        if (ThrowOnGetConnectedControllers)
        {
            throw new MissingMethodException("patched Steam input enumeration is unavailable");
        }

        GetConnectedControllersCalls++;
        var count = Math.Min(handles.Length, ConnectedControllers.Count);
        for (var index = 0; index < count; index++)
        {
            handles[index] = ConnectedControllers[index];
        }

        return count;
    }

    public static InputHandle_t GetControllerForGamepadIndex(int gamepadIndex)
    {
        return ControllersByGamepadIndex.TryGetValue(gamepadIndex, out var handle)
            ? handle
            : default;
    }

    public static ESteamInputType GetInputTypeForHandle(InputHandle_t inputHandle)
    {
        return InputTypesByHandle.TryGetValue(inputHandle, out var inputType)
            ? inputType
            : ESteamInputType.k_ESteamInputType_XBoxOneController;
    }

    public static InputDigitalActionData_t GetDigitalActionData(
        InputHandle_t inputHandle,
        InputDigitalActionHandle_t actionHandle)
    {
        return DigitalActionData.GetValueOrDefault((inputHandle, actionHandle));
    }

    public static InputAnalogActionData_t GetAnalogActionData(
        InputHandle_t inputHandle,
        InputAnalogActionHandle_t actionHandle)
    {
        return AnalogActionData.GetValueOrDefault((inputHandle, actionHandle));
    }

    public static int GetGamepadIndexForController(InputHandle_t inputHandle)
    {
        foreach (var pair in ControllersByGamepadIndex)
        {
            if (pair.Value == inputHandle)
            {
                return pair.Key;
            }
        }

        return -1;
    }
}

using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace LocalCoop.Mod.Runtime;

public static class DynamicControllerInputBridge
{
    private static readonly BindingFlags Members =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static WeakReference<object>? _inputManager;

    public static void RememberInputManager(object inputManager)
    {
        _inputManager = new WeakReference<object>(inputManager);
    }

    public static int DispatchMappedActions(object inputEvent)
    {
        return _inputManager is not null && _inputManager.TryGetTarget(out var inputManager)
            ? DispatchMappedActions(inputManager, inputEvent)
            : 0;
    }

    public static void ResetForTesting()
    {
        _inputManager = null;
    }

    public static int DispatchMappedActions(object inputManager, object inputEvent)
    {
        if (inputManager.GetType().GetField("_controllerInputMap", Members)?.GetValue(inputManager) is not IDictionary inputMap)
        {
            return 0;
        }

        var dispatched = 0;
        foreach (DictionaryEntry mapping in inputMap)
        {
            if (mapping.Key is null || mapping.Value is null)
            {
                continue;
            }

            var pressed = InvokeActionPredicate(inputEvent, "IsActionPressed", mapping.Value);
            var released = !pressed && InvokeActionPredicate(inputEvent, "IsActionReleased", mapping.Value);
            if (!pressed && !released)
            {
                continue;
            }

            var mappedEvent = Duplicate(inputEvent);
            if (mappedEvent is null
                || !SetProperty(mappedEvent, "Action", mapping.Key)
                || !SetProperty(mappedEvent, "Pressed", pressed))
            {
                continue;
            }

            SetProperty(mappedEvent, "Strength", pressed ? 1f : 0f);
            SteamControllerInputSelection.RegisterGeneratedInputEvents([mappedEvent]);
            ParseInputEvent(mappedEvent);
            dispatched++;
        }

        return dispatched;
    }

    private static bool InvokeActionPredicate(object inputEvent, string methodName, object action)
    {
        var method = inputEvent.GetType().GetMethods(Members)
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    && parameters.Length is >= 1 and <= 3
                    && parameters[0].ParameterType.IsInstanceOfType(action)
                    && parameters.Skip(1).All(parameter => parameter.ParameterType == typeof(bool));
            });
        if (method is null)
        {
            return false;
        }

        var arguments = new object[method.GetParameters().Length];
        arguments[0] = action;
        for (var index = 1; index < arguments.Length; index++)
        {
            arguments[index] = false;
        }

        return method.Invoke(inputEvent, arguments) is true;
    }

    private static object? Duplicate(object inputEvent)
    {
        return inputEvent.GetType().GetMethod("Duplicate", Members, [typeof(bool)])?.Invoke(inputEvent, [false])
            ?? inputEvent.GetType().GetMethod("Duplicate", Members, Type.EmptyTypes)?.Invoke(inputEvent, null);
    }

    private static bool SetProperty(object source, string name, object value)
    {
        var property = source.GetType().GetProperty(name, Members);
        if (property?.CanWrite is not true)
        {
            return false;
        }

        if (!property.PropertyType.IsInstanceOfType(value))
        {
            try
            {
                value = Convert.ChangeType(value, property.PropertyType);
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException)
            {
                return false;
            }
        }

        property.SetValue(source, value);
        return true;
    }

    private static void ParseInputEvent(object inputEvent)
    {
        var inputType = AccessTools.TypeByName("Godot.Input")
            ?? throw new MissingMemberException("Godot.Input");
        var method = inputType.GetMethods(Members)
            .First(candidate => string.Equals(candidate.Name, "ParseInputEvent", StringComparison.Ordinal)
                && candidate.GetParameters() is [var parameter]
                && parameter.ParameterType.IsAssignableFrom(inputEvent.GetType()));
        method.Invoke(null, [inputEvent]);
    }
}

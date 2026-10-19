using System.Reflection;

namespace LocalCoop.Mod.Runtime;

public static class DynamicControllerModeActivation
{
    private static readonly BindingFlags Members =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool TryActivate(object controllerManager, out string reason)
    {
        var property = controllerManager.GetType().GetProperty("IsUsingController", Members);
        if (property?.CanWrite is not true)
        {
            reason = "NControllerManager.IsUsingController setter unavailable";
            return false;
        }

        if (property.GetValue(controllerManager) is true)
        {
            reason = "controller mode already active";
            return true;
        }

        try
        {
            property.SetValue(controllerManager, true);
            InvokeIfPresent(controllerManager, "OnScreenContextChanged");
            InvokeIfPresent(controllerManager, "EmitSignalControllerDetected");
            InvokeIfPresent(controllerManager, "ControlModeChanged");
            reason = "controller mode activated and default-focus refresh requested";
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            reason = FormatException(exception);
            return property.GetValue(controllerManager) is true;
        }
    }

    private static void InvokeIfPresent(object instance, string methodName)
    {
        instance.GetType().GetMethods(Members)
            .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && method.GetParameters().Length == 0)
            ?.Invoke(instance, null);
    }

    private static string FormatException(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
        {
            exception = invocation.InnerException!;
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }
}

using System.Reflection;

namespace LocalCoop.Mod.Runtime;

public static class CharacterSelectInputDiagnostics
{
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static string FormatEvent(
        string typeName,
        string methodName,
        object? instance,
        object?[] args)
    {
        var parts = new List<string>
        {
            $"Character select input: {typeName}.{methodName}",
            $"selectedCharacter={SelectedCharacter(instance)}"
        };

        var buttonCharacter = TryGetCharacterId(instance);
        if (!string.IsNullOrWhiteSpace(buttonCharacter))
        {
            parts.Add($"buttonCharacter={buttonCharacter}");
        }

        foreach (var arg in args)
        {
            AppendArg(parts, arg);
        }

        return string.Join(" ", parts) + ".";
    }

    private static void AppendArg(List<string> parts, object? arg)
    {
        if (arg is null)
        {
            return;
        }

        var type = arg.GetType();
        if (type.FullName?.Contains("InputEvent", StringComparison.OrdinalIgnoreCase) == true
            || type.Name.Contains("InputEvent", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"input={type.Name}");
            AppendProperty(parts, arg, "Action", "action");
            AppendProperty(parts, arg, "Pressed", "pressed");
            AppendProperty(parts, arg, "Device", "device");
            AppendProperty(parts, arg, "ButtonIndex", "button");
            AppendProperty(parts, arg, "Axis", "axis");
            AppendProperty(parts, arg, "AxisValue", "axisValue");
            return;
        }

        var character = TryGetCharacterId(arg);
        if (!string.IsNullOrWhiteSpace(character))
        {
            parts.Add($"argCharacter={character}");
        }
    }

    private static void AppendProperty(List<string> parts, object source, string propertyName, string label)
    {
        var property = source.GetType().GetProperty(propertyName, InstanceMembers);
        if (property is null)
        {
            return;
        }

        try
        {
            var value = property.GetValue(source);
            if (value is not null)
            {
                parts.Add($"{label}={value}");
            }
        }
        catch (TargetInvocationException)
        {
        }
    }

    private static string SelectedCharacter(object? instance)
    {
        var selectedButton = GetFieldOrPropertyValue(instance, "_selectedButton")
            ?? GetFieldOrPropertyValue(instance, "SelectedButton");
        return TryGetCharacterId(selectedButton) ?? "<unknown>";
    }

    private static string? TryGetCharacterId(object? source)
    {
        if (source is null)
        {
            return null;
        }

        var character = GetFieldOrPropertyValue(source, "Character")
            ?? GetFieldOrPropertyValue(source, "_character");
        if (character is null)
        {
            character = source;
        }

        var id = GetFieldOrPropertyValue(character, "Id")
            ?? GetFieldOrPropertyValue(character, "ID")
            ?? GetFieldOrPropertyValue(character, "id");
        return id?.ToString();
    }

    private static object? GetFieldOrPropertyValue(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();
        try
        {
            var property = type.GetProperty(name, InstanceMembers);
            if (property is not null)
            {
                return property.GetValue(source);
            }

            var field = type.GetField(name, InstanceMembers);
            return field?.GetValue(source);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}

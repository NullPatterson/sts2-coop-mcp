namespace LocalCoop.Protocol;

public sealed record BrokerClientConfig(
    BrokerClientRole Role,
    int ClientIndex,
    string Host,
    int Port,
    string SessionId,
    BrokerControllerDeviceAssignment ControllerDevice = default,
    int? PlayerSlot = null,
    BrokerClientInputMode InputMode = BrokerClientInputMode.Auto,
    int? ControllerClientCount = null)
{
    public static BrokerClientConfig Parse(string content)
    {
        var values = ParseKeyValues(content);
        var role = ParseRole(Require(values, "role"));
        var clientIndex = ParseClientIndex(Require(values, "clientIndex"));
        var (host, port) = ParseEndpoint(Require(values, "endpoint"));
        var sessionId = Require(values, "sessionId");
        var legacyControllerDevice = values.TryGetValue("controllerDevice", out var controllerDeviceValue)
            ? ParseControllerDevice(controllerDeviceValue)
            : default;
        var playerSlot = values.TryGetValue("playerSlot", out var playerSlotValue)
            ? ParsePlayerSlot(playerSlotValue)
            : legacyControllerDevice.Device ?? clientIndex;
        var inputMode = values.TryGetValue("inputMode", out var inputModeValue)
            ? ParseInputMode(inputModeValue)
            : legacyControllerDevice is { IsConfigured: true, Device: null }
                ? BrokerClientInputMode.None
                : BrokerClientInputMode.Auto;
        var controllerClientCount = values.TryGetValue("controllerClientCount", out var controllerClientCountValue)
            ? ParseControllerClientCount(controllerClientCountValue)
            : (int?)null;
        var controllerDevice = inputMode == BrokerClientInputMode.None
            ? BrokerControllerDeviceAssignment.None
            : default;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new FormatException("sessionId must not be blank.");
        }

        return new BrokerClientConfig(
            role,
            clientIndex,
            host,
            port,
            sessionId,
            controllerDevice,
            playerSlot,
            inputMode,
            controllerClientCount);
    }

    private static Dictionary<string, string> ParseKeyValues(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"Invalid broker config line '{line}'. Expected key=value.");
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : throw new FormatException($"Missing broker config key '{key}'.");
    }

    private static BrokerClientRole ParseRole(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "host" => BrokerClientRole.Host,
            "client" => BrokerClientRole.Client,
            _ => throw new FormatException("role must be host or client.")
        };
    }

    private static int ParseClientIndex(string value)
    {
        if (!int.TryParse(value, out var clientIndex) || clientIndex is < 0 or > 3)
        {
            throw new FormatException("clientIndex must be an integer from 0 through 3.");
        }

        return clientIndex;
    }

    private static (string Host, int Port) ParseEndpoint(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new FormatException("endpoint must use host:port format.");
        }

        var host = value[..separator].Trim();
        if (host.Length == 0)
        {
            throw new FormatException("endpoint host must not be blank.");
        }

        if (!int.TryParse(value[(separator + 1)..], out var port) || port is <= 0 or > 65535)
        {
            throw new FormatException("endpoint port must be an integer from 1 through 65535.");
        }

        return (host, port);
    }

    private static BrokerControllerDeviceAssignment ParseControllerDevice(string value)
    {
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return BrokerControllerDeviceAssignment.None;
        }

        if (!int.TryParse(value, out var device) || device is < 0 or > 3)
        {
            throw new FormatException("controllerDevice must be none or an integer from 0 through 3.");
        }

        return BrokerControllerDeviceAssignment.ForDevice(device);
    }

    private static int ParsePlayerSlot(string value)
    {
        if (!int.TryParse(value, out var playerSlot) || playerSlot is < 0 or > 3)
        {
            throw new FormatException("playerSlot must be an integer from 0 through 3.");
        }

        return playerSlot;
    }

    private static BrokerClientInputMode ParseInputMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "auto" => BrokerClientInputMode.Auto,
            "none" => BrokerClientInputMode.None,
            _ => throw new FormatException("inputMode must be auto or none.")
        };
    }

    private static int ParseControllerClientCount(string value)
    {
        if (!int.TryParse(value, out var controllerClientCount) || controllerClientCount is < 0 or > 4)
        {
            throw new FormatException("controllerClientCount must be an integer from 0 through 4.");
        }

        return controllerClientCount;
    }
}

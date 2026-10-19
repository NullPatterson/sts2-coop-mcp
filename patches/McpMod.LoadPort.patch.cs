// ============================================================================
// Patch for McpMod.cs — replace the existing LoadPort() method with this one.
// Adds LocalCoop-aware port derivation so each co-op client process gets its
// own MCP HTTP port instead of racing for the shared default (15526).
//
// Apply by:
//   1. Deleting the existing `private static int LoadPort()` method body in
//      McpMod.cs (keep the signature).
//   2. Pasting the two methods below in its place.
//   3. Adding `using System.Linq;` to the top of McpMod.cs if not already
//      present (used by the enable-local-broker.txt line parser).
// ============================================================================

// The env var LocalCoop's BrokerModeSettings reads to find each client's own
// generated config directory (see LocalCoop.Mod/Runtime/BrokerModeSettings.cs,
// ConfigDirectoryEnvironmentVariable). We piggyback on the same variable so
// STS2_MCP and LocalCoop always agree on which client process this is,
// without needing any STS2_MCP-specific wiring in the launcher.
private const string LocalCoopConfigDirEnvVar = "LOCALCOOP_CONFIG_DIR";
private const string LocalCoopMarkerFileName = "enable-local-broker.txt";

private static int LoadPort()
{
    // 1) LocalCoop co-op mode: derive the port from clientIndex so every
    //    local client process (which all share the same mods/ install, and
    //    therefore the same STS2_MCP.dll directory) gets a distinct port
    //    without writing to any shared file.
    if (TryLoadPortFromLocalCoopConfig(out int localCoopPort))
    {
        GD.Print($"[STS2 MCP] LocalCoop co-op mode detected; using derived port {localCoopPort}.");
        return localCoopPort;
    }

    // 2) Standalone / singleplayer fallback: original STS2_MCP.conf behavior.
    try
    {
        string? modDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (modDir == null) return DefaultPort;

        string configPath = Path.Combine(modDir, ConfigFileName);
        if (!File.Exists(configPath))
        {
            try
            {
                var defaultConfig = new Dictionary<string, object> { ["port"] = DefaultPort };
                string json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
                File.WriteAllText(configPath, json);
                GD.Print($"[STS2 MCP] Created default config at {configPath}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                GD.Print($"[STS2 MCP] No config found at {configPath}; using default port {DefaultPort}");
            }
            return DefaultPort;
        }

        string content = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("port", out var portElem)
            && portElem.TryGetInt32(out int port)
            && port is > 0 and <= 65535)
        {
            return port;
        }

        GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
        return DefaultPort;
    }
    catch (Exception ex)
    {
        GD.PrintErr($"[STS2 MCP] Failed to load config: {ex.Message}, using default port {DefaultPort}");
        return DefaultPort;
    }
}

/// <summary>
/// Looks for LocalCoop's per-process config directory and, if found, reads
/// clientIndex out of its enable-local-broker.txt marker file. Returns
/// DefaultPort + clientIndex (15526, 15527, 15528, 15529 for clients 0-3).
/// Deliberately does NOT write anything to disk — every LocalCoop client
/// shares the same STS2_MCP.dll directory, so this path must stay read-only
/// and derive its answer purely from the environment.
/// </summary>
private static bool TryLoadPortFromLocalCoopConfig(out int port)
{
    port = DefaultPort;

    string? configDir = Environment.GetEnvironmentVariable(LocalCoopConfigDirEnvVar);
    if (string.IsNullOrWhiteSpace(configDir))
        return false;

    string markerPath = Path.Combine(configDir, LocalCoopMarkerFileName);
    if (!File.Exists(markerPath))
    {
        GD.PrintErr($"[STS2 MCP] {LocalCoopConfigDirEnvVar} is set to '{configDir}' but no "
            + $"{LocalCoopMarkerFileName} was found there; falling back to standalone port logic.");
        return false;
    }

    try
    {
        int? clientIndex = null;
        foreach (var rawLine in File.ReadAllLines(markerPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (string.Equals(key, "clientIndex", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out int parsedIndex))
            {
                clientIndex = parsedIndex;
                break;
            }
        }

        if (clientIndex is null)
        {
            GD.PrintErr($"[STS2 MCP] Could not find clientIndex in {markerPath}; "
                + "falling back to standalone port logic.");
            return false;
        }

        if (clientIndex is < 0 or > 3)
        {
            GD.PrintErr($"[STS2 MCP] clientIndex {clientIndex} out of the expected 0-3 range "
                + $"in {markerPath}; falling back to standalone port logic.");
            return false;
        }

        port = DefaultPort + clientIndex.Value;
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        GD.PrintErr($"[STS2 MCP] Failed to read {markerPath}: {ex.Message}; "
            + "falling back to standalone port logic.");
        return false;
    }
}

namespace LocalCoop.Mod.Runtime;

public sealed record RunIdentityDiagnosticsSettings(bool Enabled, string Reason)
{
    public const string MarkerFileName = "enable-run-identity-diagnostics.txt";
    public const string EnvironmentVariable = "LOCALCOOP_RUN_IDENTITY_DIAGNOSTICS";

    public static RunIdentityDiagnosticsSettings LoadFromDirectory(string modDirectory)
    {
        return Load(modDirectory, Environment.GetEnvironmentVariable);
    }

    public static RunIdentityDiagnosticsSettings Load(
        string modDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            throw new ArgumentException("Mod directory must not be blank.", nameof(modDirectory));
        }

        var flag = getEnvironmentVariable(EnvironmentVariable);
        if (IsTruthy(flag))
        {
            return new RunIdentityDiagnosticsSettings(true, $"{EnvironmentVariable}={flag}");
        }

        var markerPath = Path.Combine(modDirectory, MarkerFileName);
        return File.Exists(markerPath)
            ? new RunIdentityDiagnosticsSettings(true, MarkerFileName)
            : new RunIdentityDiagnosticsSettings(false, "not requested");
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }
}

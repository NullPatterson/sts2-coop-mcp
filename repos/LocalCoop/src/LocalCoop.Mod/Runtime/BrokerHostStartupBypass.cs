namespace LocalCoop.Mod.Runtime;

public static class BrokerHostStartupBypass
{
    public static bool ShouldSkipNativeHostStartup(BrokerModeSettings settings)
    {
        return settings.Enabled && settings.Config is not null;
    }

    public static bool TrySkipNativeHostStartup(
        BrokerModeSettings settings,
        string nativeMethodName,
        Action<string>? log)
    {
        if (!ShouldSkipNativeHostStartup(settings))
        {
            return false;
        }

        log?.Invoke($"Broker host startup bypass: skipped native {nativeMethodName} in broker mode.");
        return true;
    }
}

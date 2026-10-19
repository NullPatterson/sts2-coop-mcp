using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Runtime;

public static class RunIdentityDualRoleAdventureGuard
{
    public static bool ShouldSuppressForTesting(object? instance, object?[] args)
    {
        return ShouldSuppress(instance, args);
    }

    internal static bool ShouldSuppress(object? instance, object?[] args)
    {
        if (TryAlignBrokerRun(instance))
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (TryAlignBrokerRun(arg))
            {
                return true;
            }
        }

        if (RunIdentityAlignment.AlignRememberedBrokerRun())
        {
            return true;
        }

        return LocalContext.NetId is { } localNetId
            && BrokerPlayerId.ToClientIndex(localNetId) >= 0;
    }

    private static bool TryAlignBrokerRun(object? candidate)
    {
        return candidate is not null && RunIdentityAlignment.AlignBrokerRun(candidate);
    }
}

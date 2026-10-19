using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Runtime;

public static class RunIdentityRemoteMutationGuard
{
    public static bool ShouldAllowNativeMutationForTesting(object? instance, object?[] args)
    {
        return ShouldAllowNativeMutation(instance, args);
    }

    internal static bool ShouldAllowNativeMutation(object? instance, object?[] args)
    {
        if (!RunIdentityAlignment.AlignRememberedBrokerRun())
        {
            return true;
        }

        if (BrokerNetGameService.IsDispatchingNativeMessageHandler)
        {
            return true;
        }

        var localNetId = LocalContext.NetId;
        if (localNetId is null or 0)
        {
            return true;
        }

        if (!RunIdentityLocalActionGuard.TryFindPlayerNetId(instance, args, out var playerNetId)
            || playerNetId is null)
        {
            return true;
        }

        return playerNetId.Value == localNetId.Value;
    }
}

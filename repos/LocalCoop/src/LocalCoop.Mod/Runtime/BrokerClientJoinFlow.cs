using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace LocalCoop.Mod.Runtime;

public static class BrokerClientJoinFlow
{
    public static bool ShouldUseBrokerJoin(BrokerModeSettings settings)
    {
        return settings.Enabled
            && settings.Config is not null;
    }

    public static ClientLobbyJoinRequestMessage CreateJoinRequest(
        int maxAscensionUnlocked,
        SerializableUnlockState unlockState)
    {
        return new ClientLobbyJoinRequestMessage
        {
            maxAscensionUnlocked = maxAscensionUnlocked,
            unlockState = unlockState
        };
    }

    public static ClientLobbyJoinRequestMessage CreateJoinRequestFromCurrentSave(Action<string>? log)
    {
        var maxAscensionUnlocked = 0;
        var unlockState = new SerializableUnlockState();

        try
        {
            var saveManagerType = ResolveType("MegaCrit.Sts2.Core.Saves.SaveManager");
            var saveManager = saveManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (saveManager is null)
            {
                log?.Invoke("Broker client lobby handshake: SaveManager unavailable; using default join request progress.");
                return CreateJoinRequest(maxAscensionUnlocked, unlockState);
            }

            var progress = saveManager.GetType().GetProperty("Progress", BindingFlags.Public | BindingFlags.Instance)?.GetValue(saveManager);
            var maxAscension = progress?.GetType().GetProperty("MaxMultiplayerAscension", BindingFlags.Public | BindingFlags.Instance)?.GetValue(progress);
            if (maxAscension is int parsedMaxAscension)
            {
                maxAscensionUnlocked = parsedMaxAscension;
            }

            var generatedUnlockState = saveManager.GetType()
                .GetMethod("GenerateUnlockStateFromProgress", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(saveManager, null);
            var serializableUnlockState = generatedUnlockState?.GetType()
                .GetMethod("ToSerializable", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(generatedUnlockState, null);
            if (serializableUnlockState is SerializableUnlockState parsedUnlockState)
            {
                unlockState = parsedUnlockState;
            }
        }
        catch (Exception exception)
        {
            log?.Invoke($"Broker client lobby handshake: failed to read save progress; using default join request progress: {exception.GetType().Name}: {exception.Message}");
        }

        return CreateJoinRequest(maxAscensionUnlocked, unlockState);
    }

    public static async Task<JoinResult> BeginStandardBrokerJoinAsync(
        BrokerModeSettings settings,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<ClientLobbyJoinRequestMessage>? createJoinRequest = null)
    {
        if (!ShouldUseBrokerJoin(settings))
        {
            throw new InvalidOperationException("Broker join was requested while broker mode is disabled.");
        }

        var inner = BrokerNetServiceFactory.TryCreate(
            settings,
            createTransport(),
            log,
            BrokerClientRole.Client) ?? throw new InvalidOperationException("Broker client service could not be created.");
        var service = new BrokerNetGameService(inner, NetGameType.Client);
        var initialInfoSource = new TaskCompletionSource<InitialGameInfoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lobbyResponseSource = new TaskCompletionSource<ClientLobbyJoinResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadResponseSource = new TaskCompletionSource<ClientLoadJoinResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejoinResponseSource = new TaskCompletionSource<ClientRejoinResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<InitialGameInfoMessage> initialInfoHandler = initialInfo => initialInfoSource.TrySetResult(initialInfo);
        Action<ClientLobbyJoinResponseMessage> lobbyResponseHandler = response => lobbyResponseSource.TrySetResult(response);
        Action<ClientLoadJoinResponseMessage> loadResponseHandler = response => loadResponseSource.TrySetResult(response);
        Action<ClientRejoinResponseMessage> rejoinResponseHandler = response => rejoinResponseSource.TrySetResult(response);
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            initialInfoSource.TrySetCanceled(cancellationToken);
            lobbyResponseSource.TrySetCanceled(cancellationToken);
            loadResponseSource.TrySetCanceled(cancellationToken);
            rejoinResponseSource.TrySetCanceled(cancellationToken);
        });

        inner.RegisterMessageHandler(initialInfoHandler);
        inner.RegisterMessageHandler(lobbyResponseHandler);
        inner.RegisterMessageHandler(loadResponseHandler);
        inner.RegisterMessageHandler(rejoinResponseHandler);
        try
        {
            log?.Invoke($"Broker client join flow: waiting for host initial game info clientId={settings.ClientId}.");
            var initialInfo = await WaitForMessageAsync(service, initialInfoSource.Task, cancellationToken).ConfigureAwait(false);
            ThrowIfInitialGameInfoRejected(initialInfo);

            switch (initialInfo.sessionState)
            {
                case RunSessionState.InLobby:
                {
                    log?.Invoke($"Broker client join flow: sending real lobby join request clientId={settings.ClientId}.");
                    service.SendMessage(createJoinRequest?.Invoke() ?? CreateJoinRequestFromCurrentSave(log));
                    var response = await WaitForMessageAsync(service, lobbyResponseSource.Task, cancellationToken).ConfigureAwait(false);
                    service.Update();
                    BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);
                    log?.Invoke($"Broker client join flow: received host join response clientId={settings.ClientId}.");
                    return new JoinResult
                    {
                        gameMode = initialInfo.gameMode,
                        sessionState = initialInfo.sessionState,
                        joinResponse = response
                    };
                }
                case RunSessionState.InLoadedLobby:
                {
                    log?.Invoke($"Broker client join flow: sending loaded-run join request clientId={settings.ClientId}.");
                    service.SendMessage(new ClientLoadJoinRequestMessage());
                    var response = await WaitForMessageAsync(service, loadResponseSource.Task, cancellationToken).ConfigureAwait(false);
                    service.Update();
                    BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);
                    log?.Invoke($"Broker client join flow: received host loaded-run join response clientId={settings.ClientId}.");
                    return new JoinResult
                    {
                        gameMode = initialInfo.gameMode,
                        sessionState = initialInfo.sessionState,
                        loadJoinResponse = response
                    };
                }
                case RunSessionState.Running:
                {
                    log?.Invoke($"Broker client join flow: sending running-run rejoin request clientId={settings.ClientId}.");
                    service.SendMessage(new ClientRejoinRequestMessage());
                    var response = await WaitForMessageAsync(service, rejoinResponseSource.Task, cancellationToken).ConfigureAwait(false);
                    service.Update();
                    BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);
                    log?.Invoke($"Broker client join flow: received host rejoin response clientId={settings.ClientId}.");
                    return new JoinResult
                    {
                        gameMode = initialInfo.gameMode,
                        sessionState = initialInfo.sessionState,
                        rejoinResponse = response
                    };
                }
                default:
                    throw new InvalidOperationException($"Broker client join flow received unsupported session state '{initialInfo.sessionState}'.");
            }
        }
        catch
        {
            service.Dispose();
            throw;
        }
        finally
        {
            inner.UnregisterMessageHandler(initialInfoHandler);
            inner.UnregisterMessageHandler(lobbyResponseHandler);
            inner.UnregisterMessageHandler(loadResponseHandler);
            inner.UnregisterMessageHandler(rejoinResponseHandler);
        }
    }

    private static async Task<T> WaitForMessageAsync<T>(
        BrokerNetGameService service,
        Task<T> messageTask,
        CancellationToken cancellationToken)
    {
        while (!messageTask.IsCompleted)
        {
            service.Update();
            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }

        return await messageTask.ConfigureAwait(false);
    }

    private static void ThrowIfInitialGameInfoRejected(InitialGameInfoMessage initialInfo)
    {
        if (!initialInfo.connectionFailureReason.HasValue)
        {
            return;
        }

        throw new ClientConnectionFailedException(
            "Got connection failure from host",
            new NetErrorInfo(initialInfo.connectionFailureReason.Value, default));
    }

    public static bool TryTriggerBrokerJoinFromOpenedJoinScreen(
        object? screen,
        BrokerModeSettings settings,
        Action<string>? log)
    {
        if (!ShouldUseBrokerJoin(settings))
        {
            return false;
        }

        if (screen is null)
        {
            log?.Invoke("Broker join friend screen: opened join screen unavailable; broker join not triggered.");
            return false;
        }

        try
        {
            log?.Invoke("Broker join friend screen: triggering broker client join.");
            var joinGame = screen.GetType()
                .GetMethod("JoinGame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (joinGame is null)
            {
                log?.Invoke("Broker join friend screen: JoinGame method unavailable; broker join not triggered.");
                return false;
            }

            joinGame.Invoke(screen, [new PlaceholderClientConnectionInitializer()]);
            TryHideJoinScreen(screen, log);
            return true;
        }
        catch (Exception exception)
        {
            log?.Invoke($"Broker join friend screen: failed to trigger broker join: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    public static void TryHideJoinScreen(object screen, Action<string>? log)
    {
        try
        {
            TryPopJoinScreenFromStack(screen, log);
            screen.GetType()
                .GetMethod("set_Visible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(bool)])
                ?.Invoke(screen, [false]);
            log?.Invoke("Broker join friend screen: hiding join screen after broker join trigger.");
        }
        catch (Exception exception)
        {
            log?.Invoke($"Broker join friend screen: failed to hide join screen: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TryPopJoinScreenFromStack(object screen, Action<string>? log)
    {
        var stack = FindInstanceField(screen.GetType(), "_stack")?.GetValue(screen);
        if (stack is null)
        {
            return;
        }

        var peek = stack.GetType()
            .GetMethod("Peek", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(stack, null);
        if (!ReferenceEquals(peek, screen))
        {
            return;
        }

        stack.GetType()
            .GetMethod("Pop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(stack, null);
        log?.Invoke("Broker join friend screen: popped join screen from submenu stack.");
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static Type? ResolveType(string fullName)
    {
        return Type.GetType($"{fullName}, sts2", throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase))
                ?.GetType(fullName, throwOnError: false);
    }

    public sealed class PlaceholderClientConnectionInitializer : IClientConnectionInitializer
    {
        public Task<NetErrorInfo?> Connect(NetClientGameService service, CancellationToken cancelToken)
        {
            return Task.FromResult<NetErrorInfo?>(null);
        }
    }
}

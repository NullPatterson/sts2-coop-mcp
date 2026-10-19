using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;

namespace LocalCoop.Mod.Runtime;

public static class RunIdentityDiagnostics
{
    private static readonly AsyncLocal<string?> AmbientCorrelationId = new();
    private static readonly object SettingsLock = new();
    private static readonly object PeerInputDiagnosticsSamplingLock = new();
    private static readonly object DualRoleSuppressionDiagnosticsSamplingLock = new();
    private static readonly TimeSpan CorrelationCarryDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PeerInputDiagnosticsSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly Dictionary<string, long> PeerInputDiagnosticsLastLogTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> DualRoleSuppressionDiagnosticsLastLogTicks = new(StringComparer.Ordinal);
    private static long _nextCorrelationId;
    private static string? _lastShopRemoveCorrelationId;
    private static long _lastShopRemoveCorrelationTicks;
    private static bool _settingsConfigured;
    private static BrokerModeSettings? _brokerSettings;
    private static RunIdentityDiagnosticsSettings? _identitySettings;

    public static bool HasCorrelation => !string.IsNullOrWhiteSpace(AmbientCorrelationId.Value);

    public static string? CurrentCorrelationId => AmbientCorrelationId.Value;

    public static void Configure(
        BrokerModeSettings brokerSettings,
        RunIdentityDiagnosticsSettings identitySettings)
    {
        lock (SettingsLock)
        {
            _brokerSettings = brokerSettings;
            _identitySettings = identitySettings;
            _settingsConfigured = true;
        }
    }

    public static bool IsEnabled()
    {
        return TryGetEnabledSettings(out _, out _);
    }

    public static bool ShouldLogPeerInputDiagnostics(string phase, MethodBase method)
    {
        return ShouldLogPeerInputDiagnostics(phase, method, DateTimeOffset.UtcNow);
    }

    public static bool ShouldLogPeerInputDiagnosticsForTesting(
        string phase,
        MethodBase method,
        DateTimeOffset now)
    {
        return ShouldLogPeerInputDiagnostics(phase, method, now);
    }

    public static void ResetPeerInputDiagnosticsSamplingForTesting()
    {
        lock (PeerInputDiagnosticsSamplingLock)
        {
            PeerInputDiagnosticsLastLogTicks.Clear();
        }
    }

    public static bool ShouldLogDualRoleSuppressionDiagnostics(MethodBase method)
    {
        return ShouldLogDualRoleSuppressionDiagnostics(method, DateTimeOffset.UtcNow);
    }

    public static bool ShouldLogDualRoleSuppressionDiagnosticsForTesting(
        MethodBase method,
        DateTimeOffset now)
    {
        return ShouldLogDualRoleSuppressionDiagnostics(method, now);
    }

    public static void ResetDualRoleSuppressionDiagnosticsSamplingForTesting()
    {
        lock (DualRoleSuppressionDiagnosticsSamplingLock)
        {
            DualRoleSuppressionDiagnosticsLastLogTicks.Clear();
        }
    }

    public static string StartCorrelation(string kind)
    {
        var correlationId = $"{kind}-{Interlocked.Increment(ref _nextCorrelationId):000000}";
        AmbientCorrelationId.Value = correlationId;
        if (kind.Contains("shop-remove", StringComparison.OrdinalIgnoreCase))
        {
            _lastShopRemoveCorrelationId = correlationId;
            Interlocked.Exchange(ref _lastShopRemoveCorrelationTicks, DateTime.UtcNow.Ticks);
        }

        return correlationId;
    }

    public static string EnsureCorrelation(string kind)
    {
        if (!string.IsNullOrWhiteSpace(AmbientCorrelationId.Value))
        {
            return AmbientCorrelationId.Value;
        }

        if (ShouldCarryShopRemoveCorrelation(kind)
            && !string.IsNullOrWhiteSpace(_lastShopRemoveCorrelationId)
            && IsRecentShopRemoveCorrelation())
        {
            AmbientCorrelationId.Value = _lastShopRemoveCorrelationId;
            return _lastShopRemoveCorrelationId;
        }

        return StartCorrelation(kind);
    }

    private static bool ShouldLogPeerInputDiagnostics(
        string phase,
        MethodBase method,
        DateTimeOffset now)
    {
        return ShouldLogSampledDiagnostics(
            PeerInputDiagnosticsSamplingLock,
            PeerInputDiagnosticsLastLogTicks,
            phase,
            method,
            now);
    }

    private static bool ShouldLogDualRoleSuppressionDiagnostics(
        MethodBase method,
        DateTimeOffset now)
    {
        return ShouldLogSampledDiagnostics(
            DualRoleSuppressionDiagnosticsSamplingLock,
            DualRoleSuppressionDiagnosticsLastLogTicks,
            "dual-role-local-self-coop-suppressed",
            method,
            now);
    }

    private static bool ShouldLogSampledDiagnostics(
        object samplingLock,
        Dictionary<string, long> lastLogTicksByKey,
        string phase,
        MethodBase method,
        DateTimeOffset now)
    {
        var key = $"{phase}|{method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "<unknown>"}|{method.Name}";
        var nowTicks = now.UtcDateTime.Ticks;

        lock (samplingLock)
        {
            if (lastLogTicksByKey.TryGetValue(key, out var lastTicks))
            {
                var elapsedTicks = nowTicks - lastTicks;
                if (elapsedTicks >= 0 && elapsedTicks < PeerInputDiagnosticsSampleInterval.Ticks)
                {
                    return false;
                }
            }

            lastLogTicksByKey[key] = nowTicks;
            return true;
        }
    }

    public static void LogStartupSnapshot(
        BrokerModeSettings brokerSettings,
        RunIdentityDiagnosticsSettings identitySettings,
        IReadOnlyList<Type> patchTypes)
    {
        Configure(brokerSettings, identitySettings);
        if (!identitySettings.Enabled)
        {
            return;
        }

        var modDirectory = ResolveModDirectory();
        var gameRoot = ResolveGameRoot(modDirectory);
        var assemblyPath = typeof(LocalCoopMod).Assembly.Location;
        var releaseInfoPath = gameRoot is null ? null : Path.Combine(gameRoot, "release_info.json");
        var sts2Path = gameRoot is null ? null : Path.Combine(gameRoot, "data_sts2_windows_x86_64", "sts2.dll");
        var patches = string.Join(",", patchTypes.Select(type => type.FullName ?? type.Name));
        var message =
            "[lc.identity] startup " +
            $"reason={identitySettings.Reason} " +
            $"client={brokerSettings.ClientId} " +
            $"brokerEnabled={brokerSettings.Enabled} " +
            $"releaseInfo={DescribeFile(releaseInfoPath, includeText: true)} " +
            $"sts2Dll={DescribeFile(sts2Path, includeText: false)} " +
            $"localCoopDll={DescribeFile(assemblyPath, includeText: false)} " +
            $"patches=[{patches}]";

        new BrokerEventLog(brokerSettings.EventLogPath).Write(message);
    }

    public static void LogBrokerHandler(
        string phase,
        ulong localNetId,
        object netGameType,
        Type messageType,
        ulong senderId,
        object? message)
    {
        if (!TryGetEnabledSettings(out var brokerSettings, out _))
        {
            return;
        }

        Write(
            brokerSettings,
            "[lc.identity] " +
            $"cid={CorrelationForLog()} " +
            $"phase=broker-handler-{phase} " +
            $"client={brokerSettings.ClientId} " +
            $"localNetId={FormatNetId(localNetId)} " +
            $"netType={netGameType} " +
            $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
            $"senderId={FormatNetId(senderId)} " +
            $"messageType={messageType.FullName ?? messageType.Name} " +
            $"message={DescribeObject(message)}");
    }

    public static void LogBoundary(
        string phase,
        MethodBase method,
        object? instance,
        object?[] args)
    {
        if (!TryGetEnabledSettings(out var brokerSettings, out _))
        {
            return;
        }

        Write(
            brokerSettings,
            "[lc.identity] " +
            $"cid={CorrelationForLog()} " +
            $"phase={phase} " +
            $"client={brokerSettings.ClientId} " +
            $"method={DescribeMethod(method)} " +
            $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
            $"instance={DescribeObject(instance)} " +
            $"args={DescribeArguments(args)} " +
            $"run={DescribeRunSnapshot(instance)}");
    }

    public static void LogLocalUiBoundary(
        string phase,
        MethodBase method,
        object? instance,
        object?[] args)
    {
        if (!TryGetEnabledSettings(out var brokerSettings, out _))
        {
            return;
        }

        var candidates = BuildCandidates(instance, args).ToArray();
        var synchronizer = TryFindLocalPlayerSynchronizer(candidates);
        var ownerNetId = TryFindPlayerNetId(candidates);
        var optionIndex = TryFindFirstValue(candidates, ["OptionIndex", "optionIndex", "_optionIndex", "Index", "index", "_index"]);
        var actionable = TryResolveActionable(candidates);

        Write(
            brokerSettings,
            "[lc.identity] " +
            $"cid={CorrelationForLog()} " +
            $"phase={phase} " +
            $"client={brokerSettings.ClientId} " +
            $"method={DescribeMethod(method)} " +
            $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
            $"synchronizer={DescribeSynchronizer(synchronizer)} " +
            $"modelOwnerNetId={FormatNetId(ownerNetId)} " +
            $"optionIndex={FormatValue(optionIndex)} " +
            $"actionable={FormatValue(actionable)} " +
            $"instance={DescribeObject(instance)} " +
            $"args={DescribeArguments(args)} " +
            $"run={DescribeRunSnapshot(instance)}");
    }

    public static void LogResult(
        string phase,
        MethodBase method,
        object? instance,
        object?[] args,
        object? result)
    {
        if (!TryGetEnabledSettings(out var brokerSettings, out _))
        {
            return;
        }

        Write(
            brokerSettings,
            "[lc.identity] " +
            $"cid={CorrelationForLog()} " +
            $"phase={phase} " +
            $"client={brokerSettings.ClientId} " +
            $"method={DescribeMethod(method)} " +
            $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
            $"instance={DescribeObject(instance)} " +
            $"args={DescribeArguments(args)} " +
            $"result={DescribeObject(result)}");
    }

    public static void LogCorrelatedResult(
        string phase,
        MethodBase method,
        object? instance,
        object?[] args,
        object? result)
    {
        if (!HasCorrelation)
        {
            return;
        }

        LogResult(phase, method, instance, args, result);
    }

    public static void LogTaskResultWhenComplete(
        string phase,
        MethodBase method,
        object? instance,
        object?[] args,
        object? result)
    {
        if (!TryGetEnabledSettings(out var brokerSettings, out _))
        {
            return;
        }

        if (result is not Task task)
        {
            LogResult(phase, method, instance, args, result);
            return;
        }

        Write(
            brokerSettings,
            "[lc.identity] " +
            $"cid={CorrelationForLog()} " +
            $"phase={phase}-task-created " +
            $"client={brokerSettings.ClientId} " +
            $"method={DescribeMethod(method)} " +
            $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
            $"instance={DescribeObject(instance)} " +
            $"args={DescribeArguments(args)} " +
            $"taskStatus={task.Status}");

        var correlationId = AmbientCorrelationId.Value;
        _ = task.ContinueWith(
            completed =>
            {
                if (!TryGetEnabledSettings(out var continuationBrokerSettings, out _))
                {
                    return;
                }

                var previousCorrelationId = AmbientCorrelationId.Value;
                try
                {
                    AmbientCorrelationId.Value = correlationId;
                    Write(
                        continuationBrokerSettings,
                        "[lc.identity] " +
                        $"cid={CorrelationForLog()} " +
                        $"phase={phase}-task-complete " +
                        $"client={continuationBrokerSettings.ClientId} " +
                        $"method={DescribeMethod(method)} " +
                        $"localContextNetId={FormatNetId(SafeLocalContextNetId())} " +
                        $"instance={DescribeObject(instance)} " +
                        $"args={DescribeArguments(args)} " +
                        $"taskStatus={completed.Status} " +
                        $"result={DescribeTaskResult(completed)}");
                }
                finally
                {
                    AmbientCorrelationId.Value = previousCorrelationId;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static string DescribeRunSnapshot(object? instance)
    {
        if (instance is null)
        {
            return "null";
        }

        var runManager = LooksLikeRunManager(instance) ? instance : TryFindOwningRunManager(instance);
        if (runManager is null)
        {
            return "-";
        }

        var builder = new StringBuilder();
        builder.Append('{');
        AppendMember(builder, "netService", DescribeNetService(TryGetMember(runManager, "NetService")));
        AppendMember(builder, "localContextNetId", FormatNetId(SafeLocalContextNetId()));
        AppendMember(builder, "runPlayerOrder", DescribeCollection(ResolveRunPlayers(runManager)));
        AppendMember(builder, "runLobbyIds", DescribeCollection(ResolveRunLobbyIds(runManager)));
        foreach (var propertyName in RunIdentityAlignment.NativeLocalPlayerSynchronizerPropertyNames)
        {
            AppendMember(builder, propertyName, DescribeSynchronizer(TryGetMember(runManager, propertyName)));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static bool TryGetEnabledSettings(
        out BrokerModeSettings brokerSettings,
        out RunIdentityDiagnosticsSettings identitySettings)
    {
        lock (SettingsLock)
        {
            if (!_settingsConfigured)
            {
                var modDirectory = ResolveModDirectory();
                if (string.IsNullOrWhiteSpace(modDirectory))
                {
                    _brokerSettings = new BrokerModeSettings(
                        false,
                        null,
                        "client-0",
                        "localcoop-events.txt",
                        "mod directory unavailable");
                    _identitySettings = new RunIdentityDiagnosticsSettings(false, "mod directory unavailable");
                }
                else
                {
                    _brokerSettings = BrokerModeSettings.LoadFromDirectory(modDirectory);
                    _identitySettings = RunIdentityDiagnosticsSettings.LoadFromDirectory(modDirectory);
                }

                _settingsConfigured = true;
            }

            brokerSettings = _brokerSettings!;
            identitySettings = _identitySettings!;
            return identitySettings.Enabled;
        }
    }

    private static bool ShouldCarryShopRemoveCorrelation(string kind)
    {
        return kind.Contains("card-select", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("player-choice", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("is-me", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentShopRemoveCorrelation()
    {
        var ticks = Interlocked.Read(ref _lastShopRemoveCorrelationTicks);
        return ticks != 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= CorrelationCarryDuration;
    }

    private static string CorrelationForLog()
    {
        return string.IsNullOrWhiteSpace(AmbientCorrelationId.Value) ? "-" : AmbientCorrelationId.Value;
    }

    private static string? ResolveModDirectory()
    {
        var assemblyPath = typeof(LocalCoopMod).Assembly.Location;
        return string.IsNullOrWhiteSpace(assemblyPath) ? null : Path.GetDirectoryName(assemblyPath);
    }

    private static string? ResolveGameRoot(string? modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(modDirectory);
        return directory.Parent?.Parent?.FullName;
    }

    private static void Write(BrokerModeSettings brokerSettings, string message)
    {
        new BrokerEventLog(brokerSettings.EventLogPath).Write(message);
    }

    private static string DescribeFile(string? path, bool includeText)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "unavailable";
        }

        try
        {
            if (!File.Exists(path))
            {
                return $"{path}:missing";
            }

            var fileInfo = new FileInfo(path);
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var text = includeText ? $" text={Compact(File.ReadAllText(path), 180)}" : string.Empty;
            return $"{fileInfo.Name}:len={fileInfo.Length} sha256={hash}{text}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(path)}:unreadable:{exception.GetType().Name}";
        }
    }

    private static string DescribeMethod(MethodBase method)
    {
        return $"{method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "<unknown>"}.{method.Name}";
    }

    private static string DescribeArguments(object?[] args)
    {
        return "[" + string.Join(", ", args.Select(DescribeObject)) + "]";
    }

    private static string DescribeObject(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal)
        {
            return FormatSimpleValue(value);
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return DescribeCollection(enumerable);
        }

        if (LooksLikeLocalPlayerSynchronizer(value))
        {
            return DescribeSynchronizer(value);
        }

        var typeName = type.FullName ?? type.Name;
        var details = DescribeInterestingMembers(value);
        return string.IsNullOrWhiteSpace(details) ? typeName : $"{typeName}{{{details}}}";
    }

    private static string DescribeInterestingMembers(object value)
    {
        var members = new List<string>();
        foreach (var name in new[]
                 {
                     "_localPlayerId",
                     "LocalPlayer",
                     "ChoiceIds",
                     "Player",
                     "Reward",
                     "RewardType",
                     "rewardType",
                     "RewardsSetIndex",
                     "Potion",
                     "potion",
                     "potionModel",
                     "ClaimedPotion",
                     "wasSkipped",
                     "success",
                     "failureReason",
                     "HasOpenPotionSlots",
                     "MaxPotionCount",
                     "Potions",
                     "NetId",
                     "netId",
                     "Id",
                     "id",
                     "PlayerId",
                     "playerId",
                     "choiceId",
                     "ChoiceId",
                     "GoldCost",
                     "goldCost",
                     "Type",
                     "type",
                     "Character",
                     "character"
                 })
        {
            if (members.Count >= 8)
            {
                break;
            }

            var memberValue = TryGetMember(value, name);
            if (memberValue is not null)
            {
                members.Add($"{name}={FormatValue(memberValue)}");
            }
        }

        if (members.Count == 0)
        {
            var netService = TryGetMember(value, "_netService") ?? TryGetMember(value, "NetService");
            if (netService is not null)
            {
                members.Add($"netService={DescribeNetService(netService)}");
            }
        }

        return string.Join(", ", members.Distinct());
    }

    private static string DescribeTaskResult(Task task)
    {
        if (task.IsFaulted)
        {
            return DescribeObject(task.Exception?.GetBaseException());
        }

        if (task.IsCanceled)
        {
            return "canceled";
        }

        if (!task.IsCompleted)
        {
            return "not-completed";
        }

        var type = task.GetType();
        if (!type.IsGenericType)
        {
            return "completed";
        }

        try
        {
            return DescribeObject(AccessTools.Property(type, "Result")?.GetValue(task));
        }
        catch (Exception exception)
        {
            return $"unreadable-result:{exception.GetType().Name}";
        }
    }

    private static string DescribeNetService(object? service)
    {
        return service is null
            ? "null"
            : $"{service.GetType().FullName ?? service.GetType().Name}{{Type={FormatValue(TryGetMember(service, "Type"))}, NetId={FormatNetId(TryGetNetId(service))}}}";
    }

    private static string DescribeSynchronizer(object? synchronizer)
    {
        if (synchronizer is null)
        {
            return "null";
        }

        var localPlayerId = TryGetMember(synchronizer, "_localPlayerId");
        var localPlayer = ResolveSynchronizerLocalPlayer(synchronizer, localPlayerId);
        return
            $"{synchronizer.GetType().FullName ?? synchronizer.GetType().Name}" +
            $"{{_localPlayerId={FormatNetId(localPlayerId)}, LocalPlayer={DescribeObject(localPlayer)}}}";
    }

    private static object? ResolveSynchronizerLocalPlayer(object synchronizer, object? localPlayerId)
    {
        if (localPlayerId is not ulong netId)
        {
            return null;
        }

        var playerCollection = TryGetMember(synchronizer, "_playerCollection");
        if (playerCollection is null)
        {
            return null;
        }

        try
        {
            var method = AccessTools.Method(playerCollection.GetType(), "GetPlayer", [typeof(ulong)])
                ?? playerCollection.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, "GetPlayer", StringComparison.Ordinal)
                        && candidate.GetParameters().Length == 1
                        && candidate.GetParameters()[0].ParameterType == typeof(ulong));
            return method?.Invoke(playerCollection, [netId]);
        }
        catch
        {
            return null;
        }
    }

    private static object? ResolveRunPlayers(object runManager)
    {
        return TryGetMember(runManager, "Players")
            ?? TryGetMember(TryGetMember(runManager, "State"), "Players")
            ?? TryGetMember(TryGetMember(runManager, "RunState"), "Players");
    }

    private static object? ResolveRunLobbyIds(object runManager)
    {
        var lobby = TryGetMember(runManager, "RunLobby")
            ?? TryGetMember(runManager, "Lobby")
            ?? TryGetMember(runManager, "NetLobby");
        return TryGetMember(lobby, "PlayerIds")
            ?? TryGetMember(lobby, "Players")
            ?? TryGetMember(runManager, "RunLobbyIds")
            ?? TryGetMember(runManager, "LobbyIds");
    }

    private static string DescribeCollection(object? collection)
    {
        if (collection is null)
        {
            return "null";
        }

        if (collection is string text)
        {
            return Compact(text, 120);
        }

        if (collection is not IEnumerable enumerable)
        {
            return DescribeObject(collection);
        }

        var values = new List<string>();
        foreach (var value in enumerable)
        {
            if (values.Count >= 8)
            {
                values.Add("...");
                break;
            }

            values.Add(DescribeObject(value));
        }

        return "[" + string.Join(", ", values) + "]";
    }

    private static object? TryFindOwningRunManager(object instance)
    {
        return TryGetMember(instance, "RunManager")
            ?? TryGetMember(instance, "_runManager")
            ?? TryGetMember(instance, "Manager")
            ?? TryGetMember(instance, "_manager");
    }

    private static IEnumerable<object?> BuildCandidates(object? instance, object?[] args)
    {
        yield return instance;
        foreach (var arg in args)
        {
            yield return arg;
        }
    }

    private static object? TryFindLocalPlayerSynchronizer(IEnumerable<object?> candidates)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            var synchronizer = TryFindLocalPlayerSynchronizer(candidate, visited, 0);
            if (synchronizer is not null)
            {
                return synchronizer;
            }
        }

        return null;
    }

    private static object? TryFindLocalPlayerSynchronizer(object? candidate, HashSet<object> visited, int depth)
    {
        if (!CanInspect(candidate, visited, depth))
        {
            return null;
        }

        if (LooksLikeLocalPlayerSynchronizer(candidate!))
        {
            return candidate;
        }

        foreach (var memberName in new[]
                 {
                     "EventSynchronizer",
                     "RewardSynchronizer",
                     "OneOffSynchronizer",
                     "Synchronizer",
                     "synchronizer",
                     "_synchronizer",
                     "RunManager",
                     "_runManager",
                     "Owner",
                     "owner",
                     "_owner",
                     "Model",
                     "model",
                     "_model"
                 })
        {
            var synchronizer = TryFindLocalPlayerSynchronizer(TryGetMember(candidate, memberName), visited, depth + 1);
            if (synchronizer is not null)
            {
                return synchronizer;
            }
        }

        foreach (var child in EnumerateSmallCollection(candidate))
        {
            var synchronizer = TryFindLocalPlayerSynchronizer(child, visited, depth + 1);
            if (synchronizer is not null)
            {
                return synchronizer;
            }
        }

        return null;
    }

    private static ulong? TryFindPlayerNetId(IEnumerable<object?> candidates)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            var netId = TryFindPlayerNetId(candidate, visited, 0);
            if (netId is not null)
            {
                return netId;
            }
        }

        return null;
    }

    private static ulong? TryFindPlayerNetId(object? candidate, HashSet<object> visited, int depth)
    {
        if (!CanInspect(candidate, visited, depth))
        {
            return null;
        }

        if (LooksLikePlayer(candidate!) && TryGetNetId(candidate) is { } candidateNetId)
        {
            return candidateNetId;
        }

        foreach (var memberName in new[]
                 {
                     "Player",
                     "player",
                     "_player",
                     "Owner",
                     "owner",
                     "_owner",
                     "LocalPlayer",
                     "localPlayer",
                     "_localPlayer",
                     "EventOption",
                     "eventOption",
                     "_eventOption",
                     "Option",
                     "option",
                     "_option",
                     "Event",
                     "event",
                     "_event",
                     "Reward",
                     "reward",
                     "_reward",
                     "Model",
                     "model",
                     "_model"
                 })
        {
            var netId = TryFindPlayerNetId(TryGetMember(candidate, memberName), visited, depth + 1);
            if (netId is not null)
            {
                return netId;
            }
        }

        foreach (var child in EnumerateSmallCollection(candidate))
        {
            var netId = TryFindPlayerNetId(child, visited, depth + 1);
            if (netId is not null)
            {
                return netId;
            }
        }

        return null;
    }

    private static object? TryFindFirstValue(IEnumerable<object?> candidates, string[] memberNames)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is int or long or uint or ulong)
            {
                return candidate;
            }

            foreach (var memberName in memberNames)
            {
                var value = TryGetMember(candidate, memberName);
                if (value is int or long or uint or ulong)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static object? TryResolveActionable(IEnumerable<object?> candidates)
    {
        foreach (var candidate in candidates)
        {
            var explicitActionable = TryGetMember(candidate, "Actionable")
                ?? TryGetMember(candidate, "actionable")
                ?? TryGetMember(candidate, "_actionable")
                ?? TryGetMember(candidate, "IsActionable")
                ?? TryGetMember(candidate, "isActionable")
                ?? TryGetMember(candidate, "_isActionable");
            if (explicitActionable is bool)
            {
                return explicitActionable;
            }

            var enabled = TryGetMember(candidate, "Enabled")
                ?? TryGetMember(candidate, "enabled")
                ?? TryGetMember(candidate, "_enabled")
                ?? TryGetMember(candidate, "IsEnabled")
                ?? TryGetMember(candidate, "isEnabled")
                ?? TryGetMember(candidate, "_isEnabled")
                ?? TryGetMember(candidate, "Interactable")
                ?? TryGetMember(candidate, "interactable")
                ?? TryGetMember(candidate, "_interactable");
            if (enabled is bool)
            {
                return enabled;
            }

            var disabled = TryGetMember(candidate, "Disabled")
                ?? TryGetMember(candidate, "disabled")
                ?? TryGetMember(candidate, "_disabled")
                ?? TryGetMember(candidate, "IsDisabled")
                ?? TryGetMember(candidate, "isDisabled")
                ?? TryGetMember(candidate, "_isDisabled");
            if (disabled is bool isDisabled)
            {
                return !isDisabled;
            }
        }

        return null;
    }

    private static bool LooksLikeRunManager(object instance)
    {
        return string.Equals(instance.GetType().FullName, "MegaCrit.Sts2.Core.Runs.RunManager", StringComparison.Ordinal)
            || TryGetMember(instance, "NetService") is not null;
    }

    private static bool LooksLikeLocalPlayerSynchronizer(object instance)
    {
        return TryGetMember(instance, "_localPlayerId") is ulong
            && TryGetMember(instance, "_playerCollection") is not null;
    }

    private static bool LooksLikePlayer(object instance)
    {
        var type = instance.GetType();
        return string.Equals(type.Name, "Player", StringComparison.Ordinal)
            || type.Name.EndsWith("Player", StringComparison.Ordinal)
            || string.Equals(type.FullName, "MegaCrit.Sts2.Core.Entities.Players.Player", StringComparison.Ordinal);
    }

    private static bool CanInspect(object? candidate, HashSet<object> visited, int depth)
    {
        if (candidate is null || depth > 5)
        {
            return false;
        }

        var type = candidate.GetType();
        if (type.IsPrimitive || type.IsEnum || candidate is string or decimal)
        {
            return false;
        }

        return visited.Add(candidate);
    }

    private static IEnumerable<object?> EnumerateSmallCollection(object? candidate)
    {
        if (candidate is not IEnumerable enumerable || candidate is string)
        {
            yield break;
        }

        var count = 0;
        foreach (var value in enumerable)
        {
            if (count++ >= 8)
            {
                yield break;
            }

            yield return value;
        }
    }

    private static object? TryGetMember(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        try
        {
            var type = instance.GetType();
            var property = AccessTools.Property(type, name);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            var field = AccessTools.Field(type, name);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static ulong SafeLocalContextNetId()
    {
        try
        {
            return LocalContext.NetId ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static ulong? TryGetNetId(object? value)
    {
        return TryGetMember(value, "NetId") as ulong?
            ?? TryGetMember(value, "netId") as ulong?
            ?? TryGetMember(value, "_netId") as ulong?;
    }

    private static string FormatValue(object? value)
    {
        return value is null
            ? "null"
            : value is ulong netId
                ? FormatNetId(netId)
                : value is IEnumerable enumerable and not string
                    ? DescribeCollection(enumerable)
                    : FormatSimpleValue(value);
    }

    private static string FormatNetId(object? value)
    {
        return value is ulong netId ? FormatNetId(netId) : FormatSimpleValue(value);
    }

    private static string FormatNetId(ulong netId)
    {
        return $"{netId}/0x{netId:X16}";
    }

    private static string FormatSimpleValue(object? value)
    {
        return value switch
        {
            null => "null",
            string text => Compact(text, 120),
            ulong netId => FormatNetId(netId),
            _ => Compact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? value.GetType().Name, 120)
        };
    }

    private static void AppendMember(StringBuilder builder, string name, string value)
    {
        if (builder.Length > 1)
        {
            builder.Append(' ');
        }

        builder.Append(name).Append('=').Append(value);
    }

    private static string Compact(string value, int maxLength)
    {
        var compact = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "...";
    }
}

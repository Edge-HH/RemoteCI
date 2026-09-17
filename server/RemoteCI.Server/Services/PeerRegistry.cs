using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>连接注册表，同时负责命令的定向回执与权限变更后的在线连接刷新。</summary>
public sealed class PeerRegistry(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PeerRegistry> logger)
{
    private static readonly TimeSpan WatchCommandTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, WsPeer> _pluginPeers = new();
    private readonly ConcurrentDictionary<Guid, WsPeer> _watchPeers = new();
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private PluginNetworkInfo? _latestPluginNetworkInfo;
    private PluginProtocolMismatch? _latestPluginProtocolMismatch;

    public bool HasPlugin => !_pluginPeers.IsEmpty;
    public int WatchCount => _watchPeers.Values.Count(peer => peer.Principal.PeerRole == PeerRole.Watch);
    public int MobileCount => _watchPeers.Values.Count(peer => peer.Principal.PeerRole == PeerRole.Mobile);
    public int PluginCount => _pluginPeers.Count;
    public PluginProtocolMismatch? LatestPluginProtocolMismatch =>
        Volatile.Read(ref _latestPluginProtocolMismatch);

    public bool PrimaryPluginSupports(string capability) =>
        PrimaryPlugin() is { } peer && EffectiveCapabilities(peer).Contains(capability, StringComparer.Ordinal);

    public Guid Register(WebSocket socket, string token, AuthPrincipal principal)
    {
        var id = Guid.NewGuid();
        TableFor(principal.PeerRole)[id] = new WsPeer(id, token, principal, socket);
        return id;
    }

    /// <summary>保留最近一次插件协议错误，连接断开后 WebUI 仍能解释为什么显示离线。</summary>
    public void ReportPluginProtocolMismatch(string actualVersion)
    {
        var normalized = string.IsNullOrWhiteSpace(actualVersion)
            ? "缺失"
            : actualVersion.Trim()[..Math.Min(actualVersion.Trim().Length, 32)];
        Volatile.Write(ref _latestPluginProtocolMismatch, new PluginProtocolMismatch(
            normalized,
            Protocol.Version,
            timeProvider.GetUtcNow()));
    }

    /// <summary>只有插件成功发送当前协议消息后才清除旧故障，避免握手成功但消息仍不兼容时误报正常。</summary>
    public void ConfirmPluginProtocolCompatible() =>
        Volatile.Write(ref _latestPluginProtocolMismatch, null);

    /// <summary>保存当前连接显式上报的软件版本和能力；旧 V3 端未上报时继续使用基础能力。</summary>
    public async Task ReportCapabilitiesAsync(
        Guid connectionId,
        PeerCapabilities report,
        CancellationToken ct = default)
    {
        var peer = FindPeer(connectionId);
        if (peer is null) return;
        peer.SoftwareVersion = NormalizeSoftwareVersion(report.SoftwareVersion);
        peer.Capabilities = NormalizeCapabilities(report.Capabilities);
        peer.HasExplicitCapabilities = true;
        if (peer.Principal.IsPlugin)
            await BroadcastCapabilitiesToWatchesAsync(ct);
    }

    public Task SendCurrentCapabilitiesToWatchAsync(Guid connectionId, CancellationToken ct = default) =>
        SendToWatchAsync(connectionId, Envelope.CapabilitiesSync(CreateCapabilitiesSync()), ct);

    public Task BroadcastCapabilitiesToWatchesAsync(CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.CapabilitiesSync(CreateCapabilitiesSync()), ct);

    /// <summary>管理员状态页使用的连接级诊断，不包含令牌或凭据。</summary>
    public IReadOnlyList<PeerCapabilityDiagnostic> GetCapabilityDiagnostics()
    {
        var server = RemoteCiCapabilities.Current.ToHashSet(StringComparer.Ordinal);
        var primary = PrimaryPlugin();
        var primaryCapabilities = primary is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : EffectiveCapabilities(primary).ToHashSet(StringComparer.Ordinal);
        return _pluginPeers.Values.Concat(_watchPeers.Values)
            .OrderBy(peer => peer.Principal.PeerRole)
            .ThenBy(peer => peer.RegisteredAt)
            .Select(peer =>
            {
                var reported = EffectiveCapabilities(peer).ToHashSet(StringComparer.Ordinal);
                var effective = peer.Principal.IsPlugin
                    ? server.Intersect(reported, StringComparer.Ordinal)
                    : server.Intersect(primaryCapabilities, StringComparer.Ordinal)
                        .Intersect(reported, StringComparer.Ordinal);
                var effectiveArray = effective.Order(StringComparer.Ordinal).ToArray();
                return new PeerCapabilityDiagnostic(
                    peer.Id,
                    peer.Principal.PeerRole,
                    peer.Principal.User?.DisplayName ?? "ClassIsland 插件",
                    peer.SoftwareVersion,
                    peer.HasExplicitCapabilities,
                    effectiveArray,
                    server.Except(effectiveArray, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    ReferenceEquals(peer, primary));
            })
            .ToArray();
    }

    /// <summary>读取握手或后台刷新后缓存的连接身份，不访问数据库。</summary>
    public AuthPrincipal? GetPrincipal(Guid connectionId)
    {
        var peer = _pluginPeers.TryGetValue(connectionId, out var plugin) ? plugin
            : _watchPeers.TryGetValue(connectionId, out var watch) ? watch
            : null;
        return peer is not null && IsLocallyAuthorized(peer) ? peer.Principal : null;
    }

    /// <summary>凭据吊销后主动关闭对应插件连接，不等待下一条状态消息。</summary>
    public async Task DisconnectAllAsync(CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values.Concat(_watchPeers.Values).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.EndpointUnavailable);
        }
    }

    public async Task DisconnectPluginCredentialAsync(Guid credentialId, CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values
                     .Where(peer => peer.Principal.PluginCredentialId == credentialId)
                     .ToList())
        {
            ct.ThrowIfCancellationRequested();
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    public async Task UnregisterAsync(Guid connectionId, WebSocketCloseStatus? status = null)
    {
        var peer = _pluginPeers.TryRemove(connectionId, out var plugin) ? plugin
            : _watchPeers.TryRemove(connectionId, out var watch) ? watch
            : null;
        if (peer is null) return;
        var wasPlugin = peer.Principal.IsPlugin;

        foreach (var pending in _pendingCommands.Where(x => x.Value.WatchConnectionId == connectionId).ToList())
        {
            if (_pendingCommands.TryRemove(pending.Key, out var removed))
                removed.Completion?.TrySetResult(CommandResult.Failure(CommandResultCodes.Unauthorized, "连接已断开"));
        }

        var sendLockHeld = false;
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await peer.SendLock.WaitAsync(closeCts.Token);
            sendLockHeld = true;
            if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // 主动失效只发送关闭帧，不等待客户端回握，避免管理接口被远端阻塞。
                await peer.Socket.CloseOutputAsync(
                    status ?? WebSocketCloseStatus.NormalClosure, "closed", closeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            peer.Socket.Abort();
        }
        catch (WebSocketException)
        {
            // 对端已经断开，无需二次处理。
        }
        catch (ObjectDisposedException)
        {
            // 对端 socket 已释放（测试宿主或连接中断场景），同样无需二次处理。
        }
        catch (IOException)
        {
            // 连接中断（含测试宿主把已释放连接包装为 IO 异常的情况），由调用方继续清理。
        }
        finally
        {
            if (sendLockHeld) peer.SendLock.Release();
        }
        if (wasPlugin)
            await BroadcastCapabilitiesToWatchesAsync(CancellationToken.None);
    }

    public Task SendSnapshotToWatchesAsync(ClassStateSnapshot value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.StatePush(value), ct);

    public Task SendScheduleToWatchesAsync(ScheduleBundle value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ScheduleSync(value), ct);

    public Task SendScheduleSyncStatusToWatchesAsync(ScheduleSyncStatus value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ScheduleSyncStatus(value), ct);

    public Task SendEventToWatchesAsync(ClassEvent value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.EventNotify(value), ct);

    public Task SendExtensionsToWatchesAsync(IReadOnlyList<ExtensionDefinition> value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ExtensionsSync(value), ct);

    public Task SendSettingsToWatchesAsync(SettingsSync value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.SettingsSync(value), ct);

    /// <summary>缓存插件最近一次网卡发现结果，并同步给所有在线手表。</summary>
    public Task PublishPluginNetworkInfoAsync(PluginNetworkInfo value, CancellationToken ct = default)
    {
        Volatile.Write(ref _latestPluginNetworkInfo, value);
        return BroadcastWatchesAsync(Envelope.PluginNetworkInfo(value), ct);
    }

    public Task SendLatestPluginNetworkInfoToWatchAsync(Guid connectionId, CancellationToken ct = default) =>
        Volatile.Read(ref _latestPluginNetworkInfo) is { } value
            ? SendToWatchAsync(connectionId, Envelope.PluginNetworkInfo(value), ct)
            : Task.CompletedTask;

    public async Task BroadcastWatchesAsync(Envelope envelope, CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            if (!IsLocallyAuthorized(peer) || !await TrySendAsync(peer, envelope, ct))
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>广播给全部插件（幂等内容：授权镜像同步）。失效连接顺带清理。</summary>
    public async Task<bool> BroadcastToPluginsAsync(Envelope envelope, CancellationToken ct = default)
    {
        var sent = false;
        foreach (var peer in _pluginPeers.Values)
        {
            if (IsLocallyAuthorized(peer) && await TrySendAsync(peer, envelope, ct))
            {
                sent = true;
                continue;
            }

            // 发送失败说明注册表中的连接已经失效，清理后由插件自身的重连循环重新接入。
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return sent;
    }

    /// <summary>
    /// 命令与只读请求只投递给一个插件：多插件在线时选择最早接入的健康插件，
    /// 避免同一命令被多个 ClassIsland 实例重复执行（换课/关机/通知各执行一次以上）。
    /// </summary>
    public async Task<bool> SendToPluginAsync(Envelope envelope, CancellationToken ct = default)
    {
        // 按注册时间排序才是真正的“最早接入优先”；Guid 顺序与接入时间无关。
        foreach (var peer in _pluginPeers.Values.OrderBy(x => x.RegisteredAt))
        {
            if (IsLocallyAuthorized(peer) && await TrySendAsync(peer, envelope, ct))
                return true;
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return false;
    }

    public Task<bool> SendAccountSyncToPluginsAsync(AccountSync sync, CancellationToken ct = default) =>
        BroadcastToPluginsAsync(Envelope.AccountSync(sync), ct);

    /// <summary>请求最早接入的在线插件立即重新生成课表；返回是否成功发送。</summary>
    public Task<bool> RequestSchedulePullAsync(ScheduleSyncRequest request, CancellationToken ct = default) =>
        PrimaryPluginSupports(RemoteCiCapabilities.SchedulePull)
            ? SendToPluginAsync(Envelope.SchedulePull(request), ct)
            : Task.FromResult(false);

    /// <summary>
    /// 向指定插件连接发送课表拉取请求：新插件接入时的补齐拉取必须定向发给它自己，
    /// 否则多插件在线时拉取会被投递给最早的插件，新插件的启动竞态窗口无法消除。
    /// </summary>
    public async Task<bool> RequestSchedulePullFromAsync(
        Guid connectionId, ScheduleSyncRequest request, CancellationToken ct = default)
    {
        if (_pluginPeers.TryGetValue(connectionId, out var peer) && IsLocallyAuthorized(peer))
            return await TrySendAsync(peer, Envelope.SchedulePull(request), ct);
        return false;
    }

    public async Task SendToWatchAsync(Guid connectionId, Envelope envelope, CancellationToken ct = default)
    {
        if (_watchPeers.TryGetValue(connectionId, out var peer) && IsLocallyAuthorized(peer))
            await TrySendAsync(peer, envelope, ct);
    }

    public void RegisterWatchCommand(string messageId, Guid connectionId)
    {
        _pendingCommands[messageId] = new PendingCommand(connectionId, null);
        _ = ExpireWatchCommandAsync(messageId, connectionId);
    }

    /// <summary>与 REST 路径的 15 秒上限一致：插件超时未回执就回收挂起项并告知发起手表，避免永久泄漏。</summary>
    private async Task ExpireWatchCommandAsync(string messageId, Guid connectionId)
    {
        await Task.Delay(WatchCommandTimeout);
        if (!_pendingCommands.TryRemove(messageId, out var pending)) return;
        if (pending.WatchConnectionId != connectionId || pending.Completion is not null) return;
        await SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = messageId,
            Payload = CommandResult.Failure(CommandResultCodes.Timeout, "等待插件回执超时，操作结果未知"),
        });
    }

    public async Task<CommandResult> SendCommandAndWaitAsync(
        CommandMessage command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (command.Command == CommandKind.SendVoiceMessage && !VoiceMessageRequest.TryDecode(command.VoiceMessage, out _))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒");
        if (!HasPlugin) return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        if (RemoteCiCapabilities.Required(command.Command) is { } capability &&
            !PrimaryPluginSupports(capability))
            return CommandResult.Failure(
                CommandResultCodes.CapabilityUnsupported,
                $"当前主插件未声明能力 {capability}");
        var envelope = Envelope.Command(command);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[envelope.MessageId] = new PendingCommand(null, completion);
        if (!await SendToPluginAsync(envelope, ct))
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return CommandResult.Failure(CommandResultCodes.Timeout, "等待插件回执超时，操作结果未知");
        }
    }

    public async Task<bool> CompleteCommandAsync(Envelope envelope, CommandResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelope.ReplyToMessageId) ||
            !_pendingCommands.TryRemove(envelope.ReplyToMessageId, out var pending))
            return false;

        pending.Completion?.TrySetResult(result);
        if (pending.WatchConnectionId is not { } connectionId) return true;
        await SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = envelope.ReplyToMessageId,
            Payload = result,
        }, ct);
        return true;
    }

    /// <summary>低频复查全部在线连接；正常消息收发不调用数据库。</summary>
    public async Task RefreshAllAuthorizationsAsync(CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values.ToList())
        {
            if (await RefreshAsync(peer, ct) is null)
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }

        await RefreshWatchAuthorizationsAsync(ct);
    }

    public async Task RefreshWatchAuthorizationsAsync(CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            var principal = await RefreshAsync(peer, ct);
            if (principal?.User is null)
            {
                await TrySendAsync(peer, Envelope.AuthState(new AuthState
                {
                    Authenticated = false,
                    ErrorCode = ApiErrorCodes.Unauthorized,
                    Error = "账号或设备会话已失效",
                }), ct);
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
                continue;
            }
            await TrySendAsync(
                peer,
                Envelope.AuthState(ServerAuthStateFactory.CreateAuthenticated(principal.User)),
                ct);
        }
    }

    private async Task<AuthPrincipal?> RefreshAsync(WsPeer peer, CancellationToken ct)
    {
        await peer.RefreshLock.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            var principal = peer.Principal.IsPlugin
                ? await identities.ValidatePluginTokenAsync(peer.Token, ct)
                : await identities.ValidateAccessTokenAsync(peer.Token, ct);
            if (principal is not null) peer.Principal = principal;
            return principal;
        }
        catch (SqliteException ex) when (!ct.IsCancellationRequested)
        {
            // 瞬时锁或 IO 错误不能误踢健康连接；已在内存中过期的手表身份仍不得继续使用。
            logger.LogWarning("令牌校验瞬时失败，沿用未过期身份 ({Id}): {Message}", peer.Id, ex.Message);
            return IsLocallyAuthorized(peer) ? peer.Principal : null;
        }
        finally
        {
            peer.RefreshLock.Release();
        }
    }

    private WsPeer? FindPeer(Guid connectionId) =>
        _pluginPeers.TryGetValue(connectionId, out var plugin) ? plugin
            : _watchPeers.TryGetValue(connectionId, out var watch) ? watch
            : null;

    private WsPeer? PrimaryPlugin() => _pluginPeers.Values
        .Where(IsLocallyAuthorized)
        .OrderBy(peer => peer.RegisteredAt)
        .FirstOrDefault();

    private static IReadOnlyList<string> EffectiveCapabilities(WsPeer peer) =>
        peer.HasExplicitCapabilities ? peer.Capabilities : RemoteCiCapabilities.Baseline;

    private CapabilitiesSync CreateCapabilitiesSync()
    {
        var plugin = PrimaryPlugin();
        return new CapabilitiesSync
        {
            Server = AppVersion.Capabilities(),
            Plugin = plugin is null
                ? null
                : new PeerCapabilities
                {
                    SoftwareVersion = plugin.SoftwareVersion ?? string.Empty,
                    Capabilities = EffectiveCapabilities(plugin),
                },
        };
    }

    private static string? NormalizeSoftwareVersion(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized[..Math.Min(normalized.Length, 64)];
    }

    private static IReadOnlyList<string> NormalizeCapabilities(IEnumerable<string>? values) =>
        (values ?? [])
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrEmpty(value) && value.Length <= 80)
        .Distinct(StringComparer.Ordinal)
        .Take(128)
        .Cast<string>()
        .ToArray();

    private bool IsLocallyAuthorized(WsPeer peer) =>
        peer.Principal.ValidUntil is not { } validUntil || timeProvider.GetUtcNow() < validUntil;

    private ConcurrentDictionary<Guid, WsPeer> TableFor(PeerRole role) =>
        role == PeerRole.Plugin ? _pluginPeers : _watchPeers;

    private static async Task<bool> TrySendAsync(WsPeer peer, Envelope envelope, CancellationToken ct)
    {
        try
        {
            if (peer.Socket.State != WebSocketState.Open) return false;
            await peer.SendLock.WaitAsync(ct);
            try
            {
                if (peer.Socket.State != WebSocketState.Open) return false;
                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
                if (peer.Socket.State != WebSocketState.Open) return false;
                await peer.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                return true;
            }
            finally
            {
                peer.SendLock.Release();
            }
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // 对端已释放连接，等待 UnregisterAsync 清理注册表，本次发送直接跳过。
            return false;
        }
        catch (IOException)
        {
            // 连接中断（含测试宿主把已释放连接包装为 IO 异常的情况），由 UnregisterAsync 清理。
            return false;
        }
    }

    private sealed record PendingCommand(Guid? WatchConnectionId, TaskCompletionSource<CommandResult>? Completion);

    private sealed class WsPeer(Guid id, string token, AuthPrincipal principal, WebSocket socket)
    {
        public Guid Id { get; } = id;
        public string Token { get; } = token;
        public AuthPrincipal Principal { get; set; } = principal;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public SemaphoreSlim RefreshLock { get; } = new(1, 1);
        public string? SoftwareVersion { get; set; }
        public IReadOnlyList<string> Capabilities { get; set; } = [];
        public bool HasExplicitCapabilities { get; set; }
        /// <summary>接入时刻（UtcNow Ticks），用于“最早接入优先”的投递排序。</summary>
        public long RegisteredAt { get; } = DateTime.UtcNow.Ticks;
    }
}

public sealed record PeerCapabilityDiagnostic(
    Guid ConnectionId,
    PeerRole Role,
    string DisplayName,
    string? SoftwareVersion,
    bool IsExplicit,
    IReadOnlyList<string> EffectiveCapabilities,
    IReadOnlyList<string> MissingCapabilities,
    bool IsPrimaryPlugin);

public sealed record PluginProtocolMismatch(
    string ActualVersion,
    int ExpectedVersion,
    DateTimeOffset DetectedAt);

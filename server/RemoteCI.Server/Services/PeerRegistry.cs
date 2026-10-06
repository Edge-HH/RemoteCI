using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
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
    private readonly ConcurrentDictionary<Guid, PluginNetworkInfo> _latestPluginNetworkInfo = new();
    private PluginProtocolMismatch? _latestPluginProtocolMismatch;

    public bool HasPlugin => !_pluginPeers.IsEmpty;
    public int WatchCount => _watchPeers.Values.Count(peer => peer.Principal.PeerRole == PeerRole.Watch);
    public int MobileCount => _watchPeers.Values.Count(peer => peer.Principal.PeerRole == PeerRole.Mobile);
    public int PluginCount => _pluginPeers.Count;
    public PluginProtocolMismatch? LatestPluginProtocolMismatch =>
        Volatile.Read(ref _latestPluginProtocolMismatch);

    public bool HasPluginFor(Guid classId) => PluginPeersFor(classId).Any();

    public bool PrimaryPluginSupports(Guid classId, string capability) =>
        PluginPeersFor(classId).FirstOrDefault() is { } peer &&
        EffectiveCapabilities(peer).Contains(capability, StringComparer.Ordinal);

    /// <summary>归属指定班级的健康插件，最早接入优先。</summary>
    private IEnumerable<WsPeer> PluginPeersFor(Guid classId) =>
        _pluginPeers.Values
            .Where(peer => IsLocallyAuthorized(peer) && peer.Principal.ClassId == classId)
            .OrderBy(peer => peer.RegisteredAt);

    /// <summary>在线插件连接及其归属班级，供按班级生成授权镜像使用。</summary>
    public IReadOnlyList<(Guid ConnectionId, Guid ClassId)> GetOnlinePluginConnections() =>
        _pluginPeers.Values.Where(IsLocallyAuthorized)
            .Where(x => x.Principal.ClassId is { } classId)
            .OrderBy(x => x.RegisteredAt)
            .Select(x => (x.Id, x.Principal.ClassId!.Value))
            .ToList();

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

    /// <summary>保存插件上报的软件清单，并持久化到插件凭据，供设备离线时继续查看。</summary>
    public async Task ReportSoftwareInventoryAsync(
        Guid connectionId,
        SoftwareInventory report,
        CancellationToken ct = default)
    {
        var peer = FindPeer(connectionId);
        if (peer is null || !peer.Principal.IsPlugin) return;
        if (NormalizeSoftwareInventory(report) is not { } inventory) return;

        var now = timeProvider.GetUtcNow();
        peer.SoftwareInventory = inventory;
        peer.SoftwareInventoryAt = now;
        await PersistSoftwareInventoryAsync(peer, inventory, now, ct);
    }

    public async Task SendCurrentCapabilitiesToWatchAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (_watchPeers.TryGetValue(connectionId, out var peer))
            await SendToWatchAsync(connectionId, Envelope.CapabilitiesSync(CreateCapabilitiesSync(peer)), ct);
    }

    /// <summary>
    /// 能力快照按接收方可访问的班级逐个生成：多班级部署中各班插件版本与在线状态不同，
    /// 不能把全局最早接入的插件当作每个班级的能力来源。
    /// </summary>
    public async Task BroadcastCapabilitiesToWatchesAsync(CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            if (!IsLocallyAuthorized(peer) ||
                !await TrySendAsync(peer, Envelope.CapabilitiesSync(CreateCapabilitiesSync(peer)), ct))
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>管理员状态页使用的连接级诊断，不包含令牌或凭据。</summary>
    public IReadOnlyList<PeerCapabilityDiagnostic> GetCapabilityDiagnostics()
    {
        return BuildCapabilityDiagnostics(_pluginPeers.Values.Concat(_watchPeers.Values), PrimaryPlugin());
    }

    /// <summary>返回指定班级当前连接的插件诊断，不包含其他班级或手表连接。</summary>
    public IReadOnlyList<PeerCapabilityDiagnostic> GetCapabilityDiagnostics(Guid classId)
    {
        var plugins = PluginPeersFor(classId).ToArray();
        return BuildCapabilityDiagnostics(plugins, plugins.FirstOrDefault());
    }

    private static IReadOnlyList<PeerCapabilityDiagnostic> BuildCapabilityDiagnostics(
        IEnumerable<WsPeer> peers,
        WsPeer? primary)
    {
        var server = RemoteCiCapabilities.Current.ToHashSet(StringComparer.Ordinal);
        var primaryCapabilities = primary is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : EffectiveCapabilities(primary).ToHashSet(StringComparer.Ordinal);
        return peers
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

    /// <summary>返回在线插件连接及最近软件清单，包含尚未分配班级的设备。</summary>
    public IReadOnlyList<PluginDeviceSnapshot> GetPluginDeviceSnapshots() =>
        _pluginPeers.Values
            .Where(IsLocallyAuthorized)
            .OrderBy(peer => peer.Principal.ClassId)
            .ThenBy(peer => peer.RegisteredAt)
            .Select(peer => new PluginDeviceSnapshot(
                peer.Id,
                peer.Principal.PluginCredentialId,
                peer.Principal.ClassId ?? Classroom.DefaultId,
                peer.SoftwareInventory?.DeviceName ?? "ClassIsland 插件",
                peer.SoftwareVersion,
                peer.SoftwareInventoryAt,
                peer.SoftwareInventory,
                peer.HasExplicitCapabilities,
                EffectiveCapabilities(peer),
                peer.Principal.ClassId is not null))
            .ToArray();
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

    /// <summary>班级删除后断开其插件连接；凭据已随班级级联清理，插件需重新配对。</summary>
    public async Task DisconnectPluginClassAsync(Guid classId, CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values
                     .Where(peer => peer.Principal.ClassId == classId)
                     .ToList())
        {
            ct.ThrowIfCancellationRequested();
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>班级配对码只允许一台设备：新连接完成注册后主动断开旧连接。</summary>
    public async Task DisconnectOtherPluginClassAsync(Guid classId, Guid keepConnectionId, CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values
                     .Where(peer => peer.Id != keepConnectionId && peer.Principal.ClassId == classId)
                     .ToList())
        {
            ct.ThrowIfCancellationRequested();
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>
    /// 未分配设备绑定班级后：断开该班其他设备、更新本连接归属并补发授权镜像，
    /// 让设备无需重连即可立即获得对应班级的账号与数据能力。
    /// </summary>
    public async Task ReassignPluginConnectionsAsync(
        Guid credentialId, Guid classId, AccountSync sync, CancellationToken ct = default)
    {
        foreach (var peer in _pluginPeers.Values
                     .Where(peer => peer.Principal.PluginCredentialId == credentialId)
                     .ToList())
        {
            ct.ThrowIfCancellationRequested();
            await DisconnectOtherPluginClassAsync(classId, peer.Id, ct);
            peer.Principal = peer.Principal with { ClassId = classId };
            await SendToPluginConnectionAsync(peer.Id, Envelope.AccountSync(sync), ct);
        }
        await BroadcastCapabilitiesToWatchesAsync(ct);
    }

    public async Task DisconnectPluginClassAsync(IReadOnlyCollection<Guid> classIds, CancellationToken ct = default)
    {
        foreach (var classId in classIds)
        {
            ct.ThrowIfCancellationRequested();
            await DisconnectPluginClassAsync(classId, ct);
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

    public Task SendSnapshotToWatchesAsync(Guid classId, ClassStateSnapshot value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.StatePush(value), classId, ct);

    public Task SendScheduleToWatchesAsync(Guid classId, ScheduleBundle value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.ScheduleSync(value), classId, ct);

    public Task SendScheduleSyncStatusToWatchesAsync(ScheduleSyncStatus value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.ScheduleSyncStatus(value), value.ClassId, ct);

    public Task SendEventToWatchesAsync(Guid classId, ClassEvent value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.EventNotify(value), classId, ct);

    public Task SendExtensionsToWatchesAsync(Guid classId, IReadOnlyList<ExtensionDefinition> value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.ExtensionsSync(value), classId, ct);

    public Task SendSettingsToWatchesAsync(SettingsSync value, CancellationToken ct = default) =>
        BroadcastClassWatchesAsync(Envelope.SettingsSync(value), null, ct);

    /// <summary>缓存插件最近一次网卡发现结果（按班级），并同步给覆盖该班级的在线手表。</summary>
    public Task PublishPluginNetworkInfoAsync(PluginNetworkInfo value, CancellationToken ct = default)
    {
        var classId = value.ClassId ?? Classroom.DefaultId;
        _latestPluginNetworkInfo[classId] = value;
        return BroadcastClassWatchesAsync(Envelope.PluginNetworkInfo(value), classId, ct);
    }

    /// <summary>把该手表可访问班级的插件局域网信息补发给新连接。</summary>
    public async Task SendLatestPluginNetworkInfoToWatchAsync(Guid connectionId, CancellationToken ct = default)
    {
        var principal = GetPrincipal(connectionId);
        if (principal is null) return;
        foreach (var value in _latestPluginNetworkInfo.Values)
        {
            if (!principal.CoversClass(value.ClassId ?? Classroom.DefaultId)) continue;
            await SendToWatchAsync(connectionId, Envelope.PluginNetworkInfo(value), ct);
        }
    }

    /// <summary>发给某个用户全部在线的手表/手机连接（个人通知），不按班级过滤；返回投递成功的连接数。</summary>
    public async Task<int> SendToUserAsync(Guid userId, Envelope envelope, CancellationToken ct = default)
    {
        var delivered = 0;
        foreach (var peer in _watchPeers.Values)
        {
            if (peer.Principal.User?.Id != userId) continue;
            if (IsLocallyAuthorized(peer) && await TrySendAsync(peer, envelope, ct)) delivered++;
            else await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return delivered;
    }

    public async Task BroadcastWatchesAsync(Envelope envelope, CancellationToken ct = default) =>
        await BroadcastClassWatchesAsync(envelope, null, ct);

    /// <summary>
    /// 广播给手表/手机连接；classId 非空时只投递给可访问该班级的连接，
    /// 避免其他班级的课表/状态流串到无关设备上。
    /// </summary>
    private async Task BroadcastClassWatchesAsync(Envelope envelope, Guid? classId, CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            if (classId is { } target && !peer.Principal.CoversClass(target)) continue;
            if (!IsLocallyAuthorized(peer) || !await TrySendAsync(peer, envelope, ct))
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>向单个插件连接定向发送（如按班级生成的授权镜像）。</summary>
    public async Task<bool> SendToPluginConnectionAsync(Guid connectionId, Envelope envelope, CancellationToken ct = default)
    {
        if (!_pluginPeers.TryGetValue(connectionId, out var peer) || !IsLocallyAuthorized(peer)) return false;
        if (await TrySendAsync(peer, envelope, ct)) return true;
        await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        return false;
    }

    /// <summary>
    /// 向指定班级在线的插件发送：命令与只读请求只投递给该班最早接入的健康插件，
    /// 避免同一命令被多个 ClassIsland 实例重复执行（换课/关机/通知各执行一次以上）。
    /// </summary>
    public async Task<bool> SendToPluginAsync(Guid classId, Envelope envelope, CancellationToken ct = default)
    {
        // 按注册时间排序才是真正的“最早接入优先”；Guid 顺序与接入时间无关。
        foreach (var peer in _pluginPeers.Values.Where(x => x.Principal.ClassId == classId).OrderBy(x => x.RegisteredAt))
        {
            if (IsLocallyAuthorized(peer) && await TrySendAsync(peer, envelope, ct))
                return true;
            // 发送失败说明注册表中的连接已经失效，清理后由插件自身的重连循环重新接入。
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return false;
    }

    /// <summary>请求指定班级最早接入的在线插件立即重新生成课表；返回是否成功发送。</summary>
    public Task<bool> RequestSchedulePullAsync(ScheduleSyncRequest request, CancellationToken ct = default)
    {
        var classId = request.ClassId ?? Classroom.DefaultId;
        return PrimaryPluginSupports(classId, RemoteCiCapabilities.SchedulePull)
            ? SendToPluginAsync(classId, Envelope.SchedulePull(request), ct)
            : Task.FromResult(false);
    }

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

    /// <summary>服务端代发命令（REST、集控广播、WebUI）与 WebSocket 命令共用的通知参数校验。</summary>
    internal static string? GetNotificationError(CommandMessage command) =>
        command.Command == CommandKind.SendNotification ? NotificationRequest.Validate(command.Notification) : null;

    public async Task<CommandResult> SendCommandAndWaitAsync(
        CommandMessage command, Guid classId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (command.Command == CommandKind.SendVoiceMessage && !VoiceMessageRequest.TryDecode(command.VoiceMessage, out _))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒");
        if (GetNotificationError(command) is { } notificationError)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, notificationError);
        if (!HasPluginFor(classId)) return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        if (RemoteCiCapabilities.Required(command.Command) is { } capability &&
            !PrimaryPluginSupports(classId, capability))
            return CommandResult.Failure(
                CommandResultCodes.CapabilityUnsupported,
                $"当前班级的插件未声明能力 {capability}");
        var envelope = Envelope.Command(command);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[envelope.MessageId] = new PendingCommand(null, completion);
        if (!await SendToPluginAsync(classId, envelope, ct))
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

    /// <summary>
    /// 向指定插件连接发送命令并等待回执。设备页按连接批量升级时必须定向投递，
    /// 否则同一班级多台设备会重复执行或只执行最早接入的一台。
    /// </summary>
    public async Task<CommandResult> SendCommandAndWaitToConnectionAsync(
        CommandMessage command,
        Guid connectionId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (GetNotificationError(command) is { } notificationError)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, notificationError);
        if (!_pluginPeers.TryGetValue(connectionId, out var peer) || !IsLocallyAuthorized(peer))
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        if (RemoteCiCapabilities.Required(command.Command) is { } capability &&
            !EffectiveCapabilities(peer).Contains(capability, StringComparer.Ordinal))
            return CommandResult.Failure(
                CommandResultCodes.CapabilityUnsupported,
                $"当前设备插件未声明能力 {capability}");

        var envelope = Envelope.Command(command);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[envelope.MessageId] = new PendingCommand(null, completion);
        if (!await SendToPluginConnectionAsync(connectionId, envelope, ct))
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

    private CapabilitiesSync CreateCapabilitiesSync(WsPeer viewer)
    {
        var classPlugins = _pluginPeers.Values
            .Where(peer => IsLocallyAuthorized(peer) &&
                peer.Principal.ClassId is { } classId &&
                viewer.Principal.CoversClass(classId))
            .GroupBy(peer => peer.Principal.ClassId!.Value)
            .Select(group => group.OrderBy(peer => peer.RegisteredAt).First())
            .OrderBy(peer => peer.RegisteredAt)
            .Select(peer => new ClassPluginCapabilities
            {
                ClassId = peer.Principal.ClassId!.Value,
                Plugin = ToPeerCapabilities(peer),
            })
            .ToList();
        return new CapabilitiesSync
        {
            Server = AppVersion.Capabilities(),
            Plugin = classPlugins.FirstOrDefault()?.Plugin,
            ClassPlugins = classPlugins,
        };
    }

    private static PeerCapabilities ToPeerCapabilities(WsPeer plugin) => new()
    {
        SoftwareVersion = plugin.SoftwareVersion ?? string.Empty,
        Capabilities = EffectiveCapabilities(plugin),
    };

    private static string? NormalizeSoftwareVersion(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized[..Math.Min(normalized.Length, 64)];
    }

    /// <summary>限制上报清单大小与字段长度，避免恶意或异常插件把持久化数据撑爆。</summary>
    private static SoftwareInventory? NormalizeSoftwareInventory(SoftwareInventory? value)
    {
        if (value is null) return null;
        return new SoftwareInventory
        {
            DeviceName = CleanInventoryText(value.DeviceName, 80),
            OperatingSystem = CleanInventoryText(value.OperatingSystem, 200),
            Architecture = CleanInventoryText(value.Architecture, 40),
            GeneratedAt = value.GeneratedAt == default ? DateTimeOffset.UtcNow : value.GeneratedAt,
            Applications = CleanPackages(value.Applications),
            Plugins = CleanPackages(value.Plugins),
            LastUpdate = BuildSoftwareUpdateStatus(value.LastUpdate),
        };
    }

    private static string CleanInventoryText(string? text, int maxLength)
    {
        var normalized = text?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static List<SoftwarePackageInfo> CleanPackages(IEnumerable<SoftwarePackageInfo>? packages) =>
        (packages ?? [])
            .Where(package => package is not null && !string.IsNullOrWhiteSpace(package.Id))
            .Take(200)
            .Select(package => new SoftwarePackageInfo
            {
                Id = CleanInventoryText(package.Id, 200),
                Name = CleanInventoryText(package.Name, 120),
                Version = CleanInventoryText(package.Version, 64),
                LatestVersion = string.IsNullOrWhiteSpace(package.LatestVersion)
                    ? null
                    : CleanInventoryText(package.LatestVersion, 64),
                IsUpdateAvailable = package.IsUpdateAvailable,
                IsEnabled = package.IsEnabled,
                CanUpgrade = package.CanUpgrade,
            }).ToList();

    private static SoftwareUpdateStatus? BuildSoftwareUpdateStatus(SoftwareUpdateStatus? value) =>
        value is null ? null : new SoftwareUpdateStatus
        {
            Operation = value.Operation,
            State = value.State,
            Message = CleanInventoryText(value.Message, 1000),
            StartedAt = value.StartedAt == default ? DateTimeOffset.UtcNow : value.StartedAt,
            CompletedAt = value.CompletedAt,
        };

    private async Task PersistSoftwareInventoryAsync(
        WsPeer peer,
        SoftwareInventory inventory,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        if (peer.Principal.PluginCredentialId is not { } credentialId) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.Id == credentialId, ct);
            if (credential is null) return;
            credential.SoftwareInventoryJson = JsonSerializer.Serialize(inventory, JsonDefaults.Options);
            credential.SoftwareInventoryAt = updatedAt;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 连接断开导致的取消不需要记录为故障。
        }
        catch (Exception ex)
        {
            // 版本清单是诊断数据，持久化失败不能影响插件连接与命令通道。
            logger.LogWarning(ex, "持久化插件软件版本清单失败：{ConnectionId}", peer.Id);
        }
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
        public SoftwareInventory? SoftwareInventory { get; set; }
        public DateTimeOffset? SoftwareInventoryAt { get; set; }
        public bool HasExplicitCapabilities { get; set; }
        /// <summary>接入时刻（UtcNow Ticks），用于“最早接入优先”的投递排序。</summary>
        public long RegisteredAt { get; } = DateTime.UtcNow.Ticks;
    }
}

public sealed record PluginDeviceSnapshot(
    Guid ConnectionId,
    Guid? PluginCredentialId,
    Guid ClassId,
    string DisplayName,
    string? SoftwareVersion,
    DateTimeOffset? SoftwareInventoryAt,
    SoftwareInventory? SoftwareInventory,
    bool HasExplicitCapabilities,
    IReadOnlyList<string> EffectiveCapabilities,
    bool Assigned);

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

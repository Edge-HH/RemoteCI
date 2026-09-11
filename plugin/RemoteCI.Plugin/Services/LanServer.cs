using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Fleck;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>局域网接入端：一次性挑战＋设备会话 HMAC，和云端使用同一有效权限。</summary>
public sealed class LanServer : IDisposable
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly Func<CommandMessage, Task<CommandResult>> _handleCommand;
    private readonly SchedulePullRequestHandler _schedulePullRequests;
    private readonly Func<ClassStateSnapshot?> _snapshotProvider;
    private readonly Func<ScheduleBundle?> _scheduleProvider;
    private readonly ILogger<LanServer> _logger;
    private readonly ConcurrentDictionary<Guid, LanClient> _clients = new();
    private IReadOnlyList<ExtensionDefinition> _latestExtensions = [];
    private WebSocketServer? _server;
    private LanDiscoveryResponder? _discovery;

    public LanServer(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler? commands,
        Func<ScheduleSyncRequest, ScheduleSyncStatus> requestScheduleSync,
        Func<ClassStateSnapshot?> snapshotProvider,
        Func<ScheduleBundle?> scheduleProvider,
        ILogger<LanServer> logger,
        Func<CommandMessage, Task<CommandResult>>? commandHandler = null)
    {
        _settings = settings;
        _accounts = accounts;
        // 测试可注入命令执行替身；生产走 CommandHandler（唯一写操作入口）。
        _handleCommand = commandHandler
            ?? (commands is not null ? new Func<CommandMessage, Task<CommandResult>>(commands.HandleAsync) : null)
            ?? (_ => Task.FromResult(new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.InternalError,
                Message = "命令执行器未初始化",
            }));
        _schedulePullRequests = new SchedulePullRequestHandler(requestScheduleSync);
        _snapshotProvider = snapshotProvider;
        _scheduleProvider = scheduleProvider;
        _logger = logger;
    }

    public void Start()
    {
        try
        {
            var uri = $"ws://0.0.0.0:{_settings.LanServerPort}";
            _server = new WebSocketServer(uri) { RestartAfterListenError = true };
            _server.Start(socket =>
            {
                socket.OnOpen = () => OnOpened(socket);
                socket.OnMessage = message => _ = OnMessageAsync(socket, message);
                // 连接关闭时必须回收客户端（含 Gate 信号量），否则长期运行会持续泄漏信号量句柄。
                socket.OnClose = () => OnClosed(socket);
            });
        }
        catch (SocketException ex)
        {
            // 端口被占用时只停用局域网服务，不能让异常冒泡到 ClassIsland 启动流程。
            _logger.LogWarning(ex, "无法监听局域网端口 {Port}，局域网直连与设备扫描不可用", _settings.LanServerPort);
            _server?.Dispose();
            _server = null;
            return;
        }
        _discovery = new LanDiscoveryResponder(_settings, _logger);
        _discovery.Start();
        _accounts.Changed += RefreshAuthorizations;
        _logger.LogInformation("局域网 v2 服务已启动：{Uri}/ws（授权镜像版本 {Version}）", $"ws://0.0.0.0:{_settings.LanServerPort}", _accounts.Version);
    }

    public void BroadcastState(ClassStateSnapshot value) => Broadcast(Envelope.StatePush(value));
    public bool BroadcastSchedule(ScheduleBundle value) => Broadcast(Envelope.ScheduleSync(value));
    public bool BroadcastScheduleSyncStatus(ScheduleSyncStatus value) => Broadcast(Envelope.ScheduleSyncStatus(value));
    public void BroadcastEvent(ClassEvent value) => Broadcast(Envelope.EventNotify(value));
    public void BroadcastExtensions(IReadOnlyList<ExtensionDefinition> value)
    {
        // 扩展通常早于手表接入完成注册；缓存快照供后续局域网连接认证成功时补发。
        var snapshot = value.ToArray();
        Volatile.Write(ref _latestExtensions, snapshot);
        foreach (var client in AuthenticatedClients())
        {
            try
            {
                Send(client.Socket, Envelope.ExtensionsSync(VisibleExtensions(client.User!, snapshot)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "向局域网客户端 {Id} 同步扩展清单失败", client.Socket.ConnectionInfo.Id);
            }
        }
    }

    private bool Broadcast(Envelope envelope)
    {
        var sent = false;
        foreach (var client in AuthenticatedClients())
        {
            // 发送与连接关闭之间存在竞态，单个客户端失败不能影响其余客户端，
            // 更不能让异常沿每秒执行的状态推送链冒泡到 UI 线程。
            try
            {
                Send(client.Socket, envelope);
                sent = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "向局域网客户端 {Id} 广播失败", client.Socket.ConnectionInfo.Id);
            }
        }
        return sent;
    }

    internal void OnOpened(IWebSocketConnection socket)
    {
        var path = socket.ConnectionInfo.Path.TrimEnd('/');
        if (string.Equals(path, "/bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            // 未登录设备只能读取云端连接信息；密码和会话凭据始终直接交给云服务器。
            Send(socket, LanDiscoveryProtocol.CreateBootstrapEnvelope(_settings, Environment.MachineName));
            socket.Close();
            return;
        }
        if (!string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase))
        {
            socket.Close();
            return;
        }
        CleanupExpiredChallenges();
        var now = DateTimeOffset.UtcNow;
        var challenge = new AuthChallenge
        {
            ChallengeId = Guid.NewGuid().ToString("N"),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ExpiresAt = now + ChallengeTtl,
        };
        _clients[socket.ConnectionInfo.Id] = new LanClient(socket) { Challenge = challenge };
        Send(socket, Envelope.AuthChallenge(challenge));
    }

    internal void OnClosed(IWebSocketConnection socket)
    {
        if (_clients.TryRemove(socket.ConnectionInfo.Id, out var client))
            client.Dispose();
    }

    internal async Task OnMessageAsync(IWebSocketConnection socket, string message)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(message) > VoiceMessageRequest.MaxEnvelopeBytes)
        {
            socket.Close();
            return;
        }
        if (!_clients.TryGetValue(socket.ConnectionInfo.Id, out var client)) return;
        // 同一条连接的消息串行处理，避免命令并发执行与回执乱序。
        try
        {
            await client.Gate.WaitAsync(client.CloseToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // 连接已关闭、客户端已回收，排队中的消息直接丢弃。
        }
        try
        {
            await HandleMessageCoreAsync(client, message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理局域网客户端 {Id} 消息失败", socket.ConnectionInfo.Id);
        }
        finally
        {
            try { client.Gate.Release(); }
            catch (ObjectDisposedException) { /* 连接关闭与释放竞态，Gate 已回收 */ }
        }
    }

    private async Task HandleMessageCoreAsync(LanClient client, string message)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(message, JsonDefaults.Options); }
        catch (JsonException) { return; }
        if (envelope is null) return;
        if (envelope.ProtocolVersion != Protocol.Version)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = ApiErrorCodes.ProtocolVersionUnsupported,
                Error = $"需要协议 v{Protocol.Version}",
            }));
            client.Socket.Close();
            return;
        }

        if (client.User is null)
        {
            await AuthenticateAsync(client, envelope);
            return;
        }

        if (_schedulePullRequests.TryHandle(envelope)) return;
        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null) return;
        var required = CommandPermissions.Required(command.Command);
        // 授权镜像过期时所有管理命令都必须拒绝；扩展命令的静态权限为 None，
        // 因此需要单独检查镜像状态，避免绕过“仅允许查看课程”的限制。
        var mirrorExpired = !_accounts.AllowsPrivilegedOperations;
        CommandResult result;
        if (LanSessionLogic.CommandDenied(mirrorExpired, client.User.Permissions, required))
        {
            result = new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.Forbidden,
                Message = LanSessionLogic.CommandDeniedMessage(mirrorExpired),
            };
        }
        else
        {
            command.RequestedBy = client.User;
            result = await _handleCommand(command);
        }

        Send(client.Socket, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = envelope.MessageId,
            Payload = result,
        });
    }

    /// <summary>清理挑战已过期且从未完成认证的连接，避免字典被扫描流量撑大。</summary>
    private void CleanupExpiredChallenges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _clients)
        {
            if (pair.Value.User is null && pair.Value.Challenge is { } challenge && challenge.ExpiresAt <= now)
            {
                if (_clients.TryRemove(pair.Key, out var removed))
                {
                    removed.Socket.Close();
                    removed.Dispose();
                }
            }
        }
    }

    private Task AuthenticateAsync(LanClient client, Envelope envelope)
    {
        var requestError = LanSessionLogic.ValidateAuthProofRequest(envelope, client.Challenge, DateTimeOffset.UtcNow);
        if (requestError is not null || client.Challenge is not { } challenge)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = ApiErrorCodes.Unauthorized,
                Error = requestError ?? "局域网认证挑战已失效",
            }));
            client.Socket.Close();
            return Task.CompletedTask;
        }

        client.Challenge = null; // 先消费挑战，任何失败都不能重放。
        var proof = ConvertPayload<AuthProof>(envelope.Payload);
        if (proof is null || proof.ChallengeId != challenge.ChallengeId ||
            !_accounts.TryVerify(proof.DeviceSessionId, challenge, proof.ClientNonce, proof.Proof, out var user) ||
            user is null)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = ApiErrorCodes.Unauthorized,
                Error = "设备会话无效，请先通过 HTTPS 重新登录",
            }));
            client.Socket.Close();
            return Task.CompletedTask;
        }

        client.User = user;
        client.SessionId = proof.DeviceSessionId;
        SendAuthenticatedState(client, user);
        SendCapabilities(client);
        if (_snapshotProvider() is { } snapshot) Send(client.Socket, Envelope.StatePush(snapshot));
        if (_scheduleProvider() is { } schedule) Send(client.Socket, Envelope.ScheduleSync(schedule));
        Send(client.Socket, Envelope.ExtensionsSync(VisibleExtensions(user, Volatile.Read(ref _latestExtensions))));
        _logger.LogInformation("局域网设备会话已认证：{Username}/{Role}", user.Username, user.Role);
        return Task.CompletedTask;
    }

    private void RefreshAuthorizations()
    {
        foreach (var client in AuthenticatedClients())
        {
            var refreshed = _accounts.GetProfileForSession(client.User!.Id, client.SessionId);
            if (refreshed is null)
            {
                Send(client.Socket, Envelope.AuthState(new AuthState
                {
                    Authenticated = false,
                    ErrorCode = ApiErrorCodes.Unauthorized,
                    Error = "账号或设备会话已失效",
                }));
                client.Socket.Close();
                continue;
            }
            client.User = refreshed;
            SendAuthenticatedState(client, refreshed);
            SendCapabilities(client);
            Send(client.Socket, Envelope.ExtensionsSync(VisibleExtensions(refreshed, Volatile.Read(ref _latestExtensions))));
        }
    }

    private void SendAuthenticatedState(LanClient client, UserProfile user) =>
        Send(client.Socket, Envelope.AuthState(new AuthState
        {
            Authenticated = true,
            ServerVersion = _accounts.ServerVersion,
            User = user,
        }));

    private void SendCapabilities(LanClient client) =>
        Send(client.Socket, Envelope.CapabilitiesSync(new CapabilitiesSync
        {
            Server = new PeerCapabilities
            {
                SoftwareVersion = _accounts.ServerVersion,
                Capabilities = _accounts.ServerCapabilities,
            },
            Plugin = PluginAppInfo.Capabilities(),
        }));

    private IEnumerable<LanClient> AuthenticatedClients() =>
        _clients.Values.Where(x => x.User is not null && x.Socket.IsAvailable);

    private static IReadOnlyList<ExtensionDefinition> VisibleExtensions(
        UserProfile user,
        IReadOnlyList<ExtensionDefinition> extensions) =>
        extensions.Where(extension => ExtensionAccess.IsVisibleOnWatch(user, extension)).ToArray();

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    private static void Send(IWebSocketConnection socket, Envelope envelope)
    {
        if (socket.IsAvailable) socket.Send(JsonSerializer.Serialize(envelope, JsonDefaults.Options));
    }

    public void Dispose()
    {
        _accounts.Changed -= RefreshAuthorizations;
        _discovery?.Dispose();
        _discovery = null;
        _server?.Dispose();
        _server = null;
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    private sealed class LanClient(IWebSocketConnection socket) : IDisposable
    {
        private readonly CancellationTokenSource _closeCancellation = new();

        public IWebSocketConnection Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>连接关闭时取消仍在排队等待 Gate 的消息处理。</summary>
        public CancellationToken CloseToken => _closeCancellation.Token;
        public AuthChallenge? Challenge { get; set; }
        public UserProfile? User { get; set; }
        public Guid SessionId { get; set; }

        public void Dispose()
        {
            // Cancel 即可唤醒排队等待 Gate 的消息；Cancel 后不能紧跟 Dispose 该 CTS 或 Gate：
            // 已实证两者都会丢失排队等待者的取消/唤醒通知，导致排队处理永久挂起。
            // SemaphoreSlim/CTS 均无原生资源，客户端已从注册表移除后由 GC 回收。
            _closeCancellation.Cancel();
        }
    }
}

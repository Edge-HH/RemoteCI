using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

public static class WebSocketHub
{
    private const int ReceiveBufferSize = VoiceMessageRequest.MaxEnvelopeBytes;

    public static async Task HandleAsync(
        HttpContext context,
        IdentityCoordinator identities,
        PeerRegistry registry,
        IStateStore store,
        ExtensionPolicyService extensionPolicies,
        AuthorizationSyncService authorizationSync,
        ScheduleSyncService scheduleSync,
        ClassAccessService classAccess,
        ILogger logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = context.Request.Query[Protocol.QueryToken].ToString();
        var principal = await identities.ValidateAnyTokenAsync(token, context.RequestAborted);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!principal.IsPlugin &&
            string.Equals(context.Request.Query["client"], "mobile", StringComparison.OrdinalIgnoreCase))
            principal = principal with { PeerRole = PeerRole.Mobile };

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = registry.Register(socket, token, principal);
        var session = new ConnectionSession(
            context, identities, registry, store, extensionPolicies, authorizationSync, scheduleSync, classAccess,
            logger, socket, connectionId, principal);
        logger.LogInformation(
            "WebSocket connected: {Role}/{User} ({Id})",
            principal.PeerRole,
            principal.User?.Username ?? "plugin",
            connectionId);

        if (await InitializeConnectionAsync(session))
            await RunConnectionAsync(session);
    }

    private static async Task<bool> InitializeConnectionAsync(ConnectionSession session)
    {
        try
        {
            if (session.Principal.IsPlugin)
                await InitializePluginAsync(session);
            else
                await InitializeWatchAsync(session);
            return true;
        }
        catch (Exception ex)
        {
            session.Logger.LogError(ex, "WebSocket 初始化推送失败 ({Id})", session.ConnectionId);
            await session.Registry.UnregisterAsync(session.ConnectionId, WebSocketCloseStatus.InternalServerError);
            return false;
        }
    }

    private static async Task InitializePluginAsync(ConnectionSession session)
    {
        var classId = session.Principal.ClassId ?? Classroom.DefaultId;
        var sameClassPlugins = session.Registry.GetOnlinePluginConnections()
            .Count(x => x.ClassId == classId);
        if (sameClassPlugins > 1)
        {
            session.Logger.LogWarning(
                "班级 {ClassId} 检测到 {Count} 个插件同时在线，命令将只投递给最早接入的插件，请确认是否为预期部署",
                classId, sameClassPlugins);
        }

        var ct = session.CancellationToken;
        // 授权镜像按班级生成并定向发给本连接：插件只需要自己班级的账号。
        var sync = await session.Identities.CreateSyncAsync(classId, ct);
        await session.Registry.SendToPluginConnectionAsync(
            session.ConnectionId, Envelope.AccountSync(sync), ct);
        await session.Registry.BroadcastCapabilitiesToWatchesAsync(ct);
        await session.ScheduleSync.StartFromPluginAsync(
            session.ConnectionId, classId, ScheduleSyncSource.Connection, ct);
    }

    private static async Task InitializeWatchAsync(ConnectionSession session)
    {
        var ct = session.CancellationToken;
        await session.Registry.SendToWatchAsync(
            session.ConnectionId,
            Envelope.AuthState(ServerAuthStateFactory.CreateAuthenticated(session.Principal.User)),
            ct);
        await session.Registry.SendCurrentCapabilitiesToWatchAsync(session.ConnectionId, ct);
        await session.Registry.SendLatestPluginNetworkInfoToWatchAsync(session.ConnectionId, ct);

        // 该账号可访问的每个班级各推一份最新数据；消息负载带 classId 供新版客户端区分，
        // 旧客户端在单班级部署下只会收到一份，行为不变。
        foreach (var classId in session.Principal.AccessibleClassIds ?? [])
        {
            if (session.Store.GetLatestSnapshot(classId) is { } snapshot)
                await session.Registry.SendToWatchAsync(session.ConnectionId, Envelope.StatePush(snapshot), ct);
            if (session.Store.GetLatestSchedule(classId) is { } schedule)
                await session.Registry.SendToWatchAsync(session.ConnectionId, Envelope.ScheduleSync(schedule), ct);
            if (session.Store.GetLatestExtensions(classId) is { } extensions)
                await session.Registry.SendToWatchAsync(session.ConnectionId, Envelope.ExtensionsSync(extensions), ct);
            if (session.ScheduleSync.Current(classId) is { } task)
                await session.Registry.SendToWatchAsync(session.ConnectionId, Envelope.ScheduleSyncStatus(task), ct);
        }

        await session.Registry.SendToWatchAsync(
            session.ConnectionId,
            Envelope.SettingsSync(new SettingsSync
            {
                ForceSenderInTitle = await session.Identities.GetForceSenderInTitleAsync(ct),
            }),
            ct);
    }

    private static async Task RunConnectionAsync(ConnectionSession session)
    {
        try
        {
            var commandRate = new CommandRateLimiter();
            while (session.Socket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(session.Socket, session.CancellationToken);
                if (json is null) break;
                if (TryGetNonNumericProtocolVersion(json, out var nonNumericVersion))
                {
                    await RejectProtocolVersionAsync(nonNumericVersion, session);
                    break;
                }
                if (!TryDeserializeEnvelope(json, session, out var envelope)) continue;
                if (!await EnsureProtocolVersionAsync(envelope, session)) break;
                if (!await RefreshPrincipalAsync(session)) return;

                envelope.Sender = session.Principal.PeerRole;
                await DispatchAsync(envelope, session, commandRate);
            }
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            session.Logger.LogWarning("WebSocket 请求被取消: {Id}", session.ConnectionId);
        }
        catch (WebSocketException ex)
        {
            session.Logger.LogWarning("WebSocket error ({Id}): {Message}", session.ConnectionId, ex.Message);
        }
        finally
        {
            if (session.Principal.IsPlugin)
                await session.ScheduleSync.FailActiveAsync(
                    "插件连接已断开，课表任务未完成",
                    session.Principal.ClassId ?? Classroom.DefaultId);
            await session.Registry.UnregisterAsync(session.ConnectionId);
            session.Logger.LogInformation("WebSocket disconnected: {Id}", session.ConnectionId);
        }
    }

    private static bool TryDeserializeEnvelope(
        string json,
        ConnectionSession session,
        out Envelope envelope)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
            return envelope is not null;
        }
        catch (JsonException)
        {
            session.Logger.LogWarning("忽略无法解析的 WebSocket 消息 ({Id})", session.ConnectionId);
            envelope = null!;
            return false;
        }
    }

    private static async Task<bool> EnsureProtocolVersionAsync(
        Envelope envelope,
        ConnectionSession session)
    {
        if (envelope.ProtocolVersion == Protocol.Version)
        {
            if (session.Principal.IsPlugin)
                session.Registry.ConfirmPluginProtocolCompatible();
            return true;
        }

        await RejectProtocolVersionAsync(envelope.ProtocolVersion.ToString(), session);
        return false;
    }

    private static async Task RejectProtocolVersionAsync(
        string actualVersion,
        ConnectionSession session)
    {
        if (session.Principal.IsPlugin)
            session.Registry.ReportPluginProtocolMismatch(actualVersion);

        var versionError = Envelope.AuthState(new AuthState
        {
            Authenticated = false,
            ErrorCode = ApiErrorCodes.ProtocolVersionUnsupported,
            Error = $"需要协议 v{Protocol.Version}，当前协议为 {FormatProtocolVersion(actualVersion)}",
        });
        if (session.Principal.IsPlugin)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(versionError, JsonDefaults.Options);
            await session.Socket.SendAsync(
                bytes, WebSocketMessageType.Text, true, session.CancellationToken);
        }
        else
        {
            await session.Registry.SendToWatchAsync(
                session.ConnectionId, versionError, session.CancellationToken);
        }
    }

    private static string FormatProtocolVersion(string value) =>
        string.Equals(value, "缺失", StringComparison.Ordinal) ? value : $"v{value}";

    /// <summary>整数之外的声明无法反序列化为 Envelope，先从原始 JSON 提取以便给 WebUI 留下诊断。</summary>
    private static bool TryGetNonNumericProtocolVersion(string json, out string actualVersion)
    {
        actualVersion = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("protocolVersion", out var version))
            {
                actualVersion = "缺失";
                return true;
            }
            if (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out _))
                return false;
            actualVersion = version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? "缺失"
                : version.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> RefreshPrincipalAsync(ConnectionSession session)
    {
        var refreshed = session.Registry.GetPrincipal(session.ConnectionId);
        if (refreshed is not null)
        {
            session.Principal = refreshed;
            return true;
        }

        await session.Registry.UnregisterAsync(
            session.ConnectionId, WebSocketCloseStatus.PolicyViolation);
        return false;
    }

    private static async Task DispatchAsync(
        Envelope envelope,
        ConnectionSession session,
        CommandRateLimiter commandRate)
    {
        if (envelope.Type == Protocol.MessageTypePeerCapabilities)
        {
            if (ConvertPayload<PeerCapabilities>(envelope.Payload) is { } capabilities)
                await session.Registry.ReportCapabilitiesAsync(
                    session.ConnectionId, capabilities, session.CancellationToken);
            return;
        }

        if (session.Principal.IsPlugin)
        {
            await DispatchPluginAsync(envelope, session);
            return;
        }

        if (session.Principal.User is not null)
        {
            await DispatchUserAsync(envelope, session, commandRate);
            return;
        }

        LogUnhandled(envelope, session);
    }

    private static async Task DispatchPluginAsync(Envelope envelope, ConnectionSession session)
    {
        if (await TryDispatchPluginStateAsync(envelope, session)) return;
        if (await TryDispatchPluginControlAsync(envelope, session)) return;
        LogUnhandled(envelope, session);
    }

    private static async Task<bool> TryDispatchPluginStateAsync(
        Envelope envelope,
        ConnectionSession session)
    {
        var ct = session.CancellationToken;
        var classId = session.Principal.ClassId ?? Classroom.DefaultId;
        switch (envelope.Type)
        {
            case Protocol.MessageTypeStatePush:
                if (ConvertPayload<ClassStateSnapshot>(envelope.Payload) is { } snapshot)
                {
                    // 负载携带班级标识，客户端据此区分多个班级的推送。
                    snapshot.ClassId = classId;
                    session.Store.SaveSnapshot(classId, snapshot);
                    await session.Registry.SendSnapshotToWatchesAsync(classId, snapshot, ct);
                }
                return true;
            case Protocol.MessageTypeScheduleSync:
                if (ConvertPayload<ScheduleBundle>(envelope.Payload) is { } schedule)
                {
                    schedule.ClassId = classId;
                    session.Store.SaveSchedule(classId, schedule);
                    await session.Registry.SendScheduleToWatchesAsync(classId, schedule, ct);
                    await session.ScheduleSync.CompleteFromScheduleAsync(classId, ct);
                }
                return true;
            case Protocol.MessageTypeEventNotify:
                if (ConvertPayload<ClassEvent>(envelope.Payload) is { } value)
                {
                    value.ClassId = classId;
                    session.Store.SaveEvent(classId, value);
                    await session.Registry.SendEventToWatchesAsync(classId, value, ct);
                }
                return true;
            case Protocol.MessageTypeExtensionsSync:
                if (ConvertPayload<List<ExtensionDefinition>>(envelope.Payload) is { } extensions)
                {
                    foreach (var definition in extensions) definition.ClassId = classId;
                    var accessChanged = await session.ExtensionPolicies.EnsureRegisteredAsync(extensions, ct);
                    session.Store.SaveExtensions(classId, extensions);
                    if (accessChanged)
                        await session.AuthorizationSync.SyncAsync(ct);
                    await session.Registry.SendExtensionsToWatchesAsync(classId, extensions, ct);
                }
                return true;
            default:
                return false;
        }
    }

    private static async Task<bool> TryDispatchPluginControlAsync(
        Envelope envelope,
        ConnectionSession session)
    {
        var ct = session.CancellationToken;
        switch (envelope.Type)
        {
            case Protocol.MessageTypePluginNetworkInfo:
                if (NormalizePluginNetworkInfo(ConvertPayload<PluginNetworkInfo>(envelope.Payload)) is { } info)
                {
                    info.ClassId = session.Principal.ClassId ?? Classroom.DefaultId;
                    await session.Registry.PublishPluginNetworkInfoAsync(info, ct);
                }
                else
                    session.Logger.LogWarning("插件上报了无效的局域网地址或端口");
                return true;
            case Protocol.MessageTypeScheduleSyncStatus:
                if (ConvertPayload<ScheduleSyncStatus>(envelope.Payload) is { } status)
                    await session.ScheduleSync.ObserveFromPluginAsync(status, ct);
                return true;
            case Protocol.MessageTypeCommandResult:
                if (ConvertPayload<CommandResult>(envelope.Payload) is { } result)
                    await session.Registry.CompleteCommandAsync(envelope, result, ct);
                return true;
            default:
                return false;
        }
    }

    private static async Task DispatchUserAsync(
        Envelope envelope,
        ConnectionSession session,
        CommandRateLimiter commandRate)
    {
        switch (envelope.Type)
        {
            case Protocol.MessageTypeCommand:
                await ForwardUserCommandAsync(envelope, session, commandRate);
                return;
            case Protocol.MessageTypeSchedulePull:
                var request = ConvertPayload<ScheduleSyncRequest>(envelope.Payload);
                var source = request?.Source == ScheduleSyncSource.Mobile
                    ? ScheduleSyncSource.Mobile
                    : ScheduleSyncSource.Watch;
                var classId = await ResolveClassAsync(request?.ClassId, session);
                if (classId is null) return;
                request ??= new ScheduleSyncRequest { Source = source };
                request.ClassId = classId;
                await session.ScheduleSync.StartAsync(
                    source,
                    classId.Value,
                    session.CancellationToken,
                    request.TaskId);
                return;
            default:
                LogUnhandled(envelope, session);
                return;
        }
    }

    private static void LogUnhandled(Envelope envelope, ConnectionSession session) =>
        session.Logger.LogWarning(
            "Unhandled message type {Type} from {Role}",
            envelope.Type,
            session.Principal.PeerRole);

    /// <summary>解析命令/拉取请求的目标班级：显式指定时校验可访问性，否则落到默认班级。</summary>
    private static async Task<Guid?> ResolveClassAsync(Guid? requested, ConnectionSession session)
    {
        var user = session.Principal.User!;
        if (requested is { } classId)
            return await session.ClassAccess.CanAccessAsync(user.Id, user.Role, classId, session.CancellationToken)
                ? classId
                : null;
        return await session.ClassAccess.ResolveDefaultClassIdAsync(user.Id, user.Role, session.CancellationToken);
    }

    private static async Task ForwardUserCommandAsync(
        Envelope envelope,
        ConnectionSession session,
        CommandRateLimiter commandRate)
    {
        if (string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            session.Logger.LogWarning(
                "忽略缺少 messageId 的命令 ({ConnectionId})", session.ConnectionId);
            return;
        }
        if (!commandRate.TryAcquire())
        {
            await SendFailureAsync(
                envelope, session.ConnectionId, session.Registry,
                CommandResultCodes.TooManyRequests, "命令发送过于频繁", session.CancellationToken);
            return;
        }

        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null)
        {
            await SendFailureAsync(
                envelope, session.ConnectionId, session.Registry,
                CommandResultCodes.InvalidRequest, "命令格式无效", session.CancellationToken);
            return;
        }

        // 命令按班级路由与鉴权：用户只能在有权限的班级上操作该班的插件。
        var classId = await ResolveClassAsync(command.ClassId, session);
        if (classId is null)
        {
            await SendFailureAsync(
                envelope, session.ConnectionId, session.Registry,
                CommandResultCodes.Forbidden, "没有该班级的操作权限", session.CancellationToken);
            return;
        }
        command.ClassId = classId;

        if (command.Notification is not null)
        {
            command.Notification.ForceSenderInTitle =
                await session.Identities.GetForceSenderInTitleAsync(session.CancellationToken);
        }

        var classPermissions = await session.ClassAccess.GetEffectivePermissionsAsync(
            session.Principal.User!.Id, session.Principal.User!.Role, classId.Value,
            session.Principal.User!.GrantedPermissions, session.CancellationToken);
        if (GetCommandValidationError(
                command, classPermissions, session.Principal.User!.AllowedExtensionIds,
                session.Store, classId.Value) is { } error)
        {
            await SendFailureAsync(
                envelope, session.ConnectionId, session.Registry,
                error.Code, error.Message, session.CancellationToken);
            return;
        }

        if (session.Registry.HasPluginFor(classId.Value) &&
            RemoteCiCapabilities.Required(command.Command) is { } capability &&
            !session.Registry.PrimaryPluginSupports(classId.Value, capability))
        {
            await SendFailureAsync(
                envelope, session.ConnectionId, session.Registry,
                CommandResultCodes.CapabilityUnsupported,
                $"当前班级的插件未声明能力 {capability}", session.CancellationToken);
            return;
        }

        await ForwardValidatedCommandAsync(envelope, command, session, classId.Value);
    }

    private static CommandError? GetCommandValidationError(
        CommandMessage command,
        UserPermissions classPermissions,
        IReadOnlyList<string>? allowedExtensionIds,
        IStateStore store,
        Guid classId)
    {
        if (command.Command == CommandKind.RunExtension)
            return GetExtensionValidationError(command, classPermissions, allowedExtensionIds, store, classId);

        var required = CommandPermissions.Required(command.Command);
        if (required == UserPermissions.None)
            return new CommandError(CommandResultCodes.InvalidRequest, "未知命令");
        if (!classPermissions.HasFlag(required))
            return new CommandError(CommandResultCodes.Forbidden, "权限不足");
        if (command.Command == CommandKind.SendVoiceMessage && !VoiceMessageRequest.TryDecode(command.VoiceMessage, out _))
            return new CommandError(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒");
        return null;
    }

    private static CommandError? GetExtensionValidationError(
        CommandMessage command,
        UserPermissions classPermissions,
        IReadOnlyList<string>? allowedExtensionIds,
        IStateStore store,
        Guid classId)
    {
        if (string.IsNullOrWhiteSpace(command.ExtensionId))
            return new CommandError(CommandResultCodes.InvalidRequest, "缺少扩展 Id");

        var definition = store.GetLatestExtensions(classId)?.FirstOrDefault(
            extension => extension.Id == command.ExtensionId);
        if (definition is null)
            return new CommandError(CommandResultCodes.InvalidRequest, "扩展功能不存在或尚未同步");
        return ExtensionAccess.CanInvoke(classPermissions, allowedExtensionIds, definition)
            ? null
            : new CommandError(CommandResultCodes.Forbidden, "权限不足或扩展未开放");
    }

    private static async Task ForwardValidatedCommandAsync(
        Envelope envelope,
        CommandMessage command,
        ConnectionSession session,
        Guid classId)
    {
        command.RequestedBy = session.Principal.User;
        envelope.Payload = command;
        session.Registry.RegisterWatchCommand(envelope.MessageId, session.ConnectionId);
        if (await session.Registry.SendToPluginAsync(classId, envelope, session.CancellationToken)) return;

        await session.Registry.CompleteCommandAsync(
            new Envelope
            {
                Type = Protocol.MessageTypeCommandResult,
                ReplyToMessageId = envelope.MessageId,
                Payload = new CommandResult(),
            },
            new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.PluginOffline,
                Message = "插件未在线，操作未执行",
            },
            session.CancellationToken);
    }

    private sealed record CommandError(string Code, string Message);

    private sealed class ConnectionSession(
        HttpContext context,
        IdentityCoordinator identities,
        PeerRegistry registry,
        IStateStore store,
        ExtensionPolicyService extensionPolicies,
        AuthorizationSyncService authorizationSync,
        ScheduleSyncService scheduleSync,
        ClassAccessService classAccess,
        ILogger logger,
        WebSocket socket,
        Guid connectionId,
        AuthPrincipal principal)
    {
        public HttpContext Context { get; } = context;
        public IdentityCoordinator Identities { get; } = identities;
        public PeerRegistry Registry { get; } = registry;
        public IStateStore Store { get; } = store;
        public ExtensionPolicyService ExtensionPolicies { get; } = extensionPolicies;
        public AuthorizationSyncService AuthorizationSync { get; } = authorizationSync;
        public ScheduleSyncService ScheduleSync { get; } = scheduleSync;
        public ClassAccessService ClassAccess { get; } = classAccess;
        public ILogger Logger { get; } = logger;
        public WebSocket Socket { get; } = socket;
        public Guid ConnectionId { get; } = connectionId;
        public AuthPrincipal Principal { get; set; } = principal;
        public CancellationToken CancellationToken => Context.RequestAborted;
    }

    private static Task SendFailureAsync(
        Envelope request, Guid connectionId, PeerRegistry registry, string code, string message, CancellationToken ct) =>
        registry.SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = request.MessageId,
            Payload = new CommandResult { Success = false, Code = code, Message = message },
        }, ct);

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    private static PluginNetworkInfo? NormalizePluginNetworkInfo(PluginNetworkInfo? value)
    {
        if (value is null || value.Port is < 1 or > 65535) return null;
        var addresses = value.Addresses
            .Select(address => address?.Trim())
            .Where(address => !string.IsNullOrEmpty(address) && System.Net.IPAddress.TryParse(address, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Cast<string>()
            .ToArray();
        if (value.LanServerEnabled && addresses.Length == 0) return null;
        return new PluginNetworkInfo
        {
            LanServerEnabled = value.LanServerEnabled,
            Addresses = addresses,
            Port = value.Port,
        };
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Binary)
                throw new WebSocketException("不支持二进制帧");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > ReceiveBufferSize) throw new WebSocketException("消息过大");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 单连接命令滑动窗口限速：防止单个手表连接洪泛命令把插件打爆。
    /// 限速器由每个连接的接收循环独享，无需跨连接共享状态。
    /// </summary>
    private sealed class CommandRateLimiter
    {
        private const int MaxCommandsPerWindow = 20;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private readonly Queue<long> _timestamps = new();

        public bool TryAcquire()
        {
            var now = Environment.TickCount64;
            while (_timestamps.Count > 0 && now - _timestamps.Peek() > Window.TotalMilliseconds)
                _timestamps.Dequeue();
            if (_timestamps.Count >= MaxCommandsPerWindow) return false;
            _timestamps.Enqueue(now);
            return true;
        }
    }
}

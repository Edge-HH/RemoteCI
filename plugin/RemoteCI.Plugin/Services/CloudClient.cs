using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>插件云端通道：长期插件凭据、版本化授权同步和真实命令回执。</summary>
public sealed class CloudClient : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CredentiallessRetryDelay = TimeSpan.FromMinutes(2);
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CloudTokenStore? _tokenStore;
    private readonly Func<CommandMessage, Task<CommandResult>> _handleCommand;
    private readonly SchedulePullRequestHandler _schedulePullRequests;
    private readonly ILogger<CloudClient> _logger;
    private readonly Func<ICloudSocket> _socketFactory;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _connectionTestTimeout;
    // 配对请求设置显式超时，服务器挂起时不会让重连循环永久阻塞。
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _retrySignal = new(0, 1);
    private readonly SemaphoreSlim _connectionTestLock = new(1, 1);
    private ICloudSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private TimeSpan _reconnectDelay;
    private string? _connectedServerDisplayName;
    private CloudConnectionStatus _currentStatus = CloudConnectionStatus.Stopped();

    /// <summary>云端 WebSocket 完成连接且可以发送状态时触发，重连后也会再次触发。</summary>
    public event EventHandler? Connected;

    /// <summary>连接阶段或最近错误变化时触发，供插件设置页实时展示。</summary>
    public event Action<CloudConnectionStatus>? ConnectionStatusChanged;

    public CloudConnectionStatus CurrentStatus => Volatile.Read(ref _currentStatus);

    internal CloudClient(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler? commands,
        Func<ScheduleSyncRequest, ScheduleSyncStatus> requestScheduleSync,
        ILogger<CloudClient> logger,
        CloudTokenStore? tokenStore = null,
        Func<ICloudSocket>? socketFactory = null,
        HttpMessageHandler? httpHandler = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? connectionTestTimeout = null)
    {
        _settings = settings;
        _accounts = accounts;
        _tokenStore = tokenStore;
        // 测试可注入命令执行替身；生产走 CommandHandler（唯一写操作入口）。
        _handleCommand = commands is not null
            ? commands.HandleAsync
            : _ => Task.FromResult(new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.InternalError,
                Message = "命令执行器未初始化",
            });
        _schedulePullRequests = new SchedulePullRequestHandler(requestScheduleSync);
        _logger = logger;
        _socketFactory = socketFactory ?? (() => new ClientWebSocketAdapter(new ClientWebSocket()));
        _http = new HttpClient(httpHandler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        _initialReconnectDelay = reconnectDelay ?? ReconnectDelay;
        _reconnectDelay = _initialReconnectDelay;
        _connectionTestTimeout = connectionTestTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>长期凭据访问器：内存缓存 + DPAPI 落盘（凭据吊销时同步删除存储文件）。</summary>
    private string? Token
    {
        get => _settings.CloudToken;
        set
        {
            _settings.CloudToken = value;
            _tokenStore?.Save(value);
        }
    }

    public Task StartAsync(CancellationToken stoppingToken = default)
    {
        if (!_settings.EnableCloud)
        {
            UpdateStatus(CloudConnectionState.Disabled, "云端连接已在开发者设置中关闭");
            return Task.CompletedTask;
        }
        if (_running) return Task.CompletedTask;
        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        UpdateStatus(CloudConnectionState.Connecting, $"正在连接 {ServerDisplayName()}");
        _ = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task SendStateAsync(ClassStateSnapshot value) => SendAsync(Envelope.StatePush(value));
    public Task<bool> SendScheduleAsync(ScheduleBundle value) => TrySendAsync(Envelope.ScheduleSync(value));
    public Task<bool> SendScheduleSyncStatusAsync(ScheduleSyncStatus value) => TrySendAsync(Envelope.ScheduleSyncStatus(value));
    public Task SendEventAsync(ClassEvent value) => SendAsync(Envelope.EventNotify(value));
    public Task SendExtensionsAsync(IReadOnlyList<ExtensionDefinition> value) => SendAsync(Envelope.ExtensionsSync(value));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                UpdateStatus(CloudConnectionState.Connecting, $"正在连接 {ServerDisplayName()}");
                await EnsureTokenAsync(ct);
                await ConnectAsync(ct);
                _reconnectDelay = _initialReconnectDelay; // 连接成功即复位退避。
                await ReceiveLoopAsync(ct);
                if (_running && !ct.IsCancellationRequested)
                    throw new WebSocketException("服务端已关闭 WebSocket 连接");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_running || ct.IsCancellationRequested) break;
                var authenticationFailure = IsAuthenticationFailure(ex);
                if (authenticationFailure)
                {
                    Token = null;
                    _logger.LogWarning("插件云端凭据已失效，请在 WebUI 生成新的一次性配对码");
                }
                else
                {
                    _logger.LogWarning(ex, "云端连接异常，{Delay} 秒后重连", _reconnectDelay.TotalSeconds);
                }
                DisposeSocket();
                // 没有任何凭据时退避到 2 分钟，避免每 5 秒刷日志空转；循环仍会周期性拾取新填写的配对码。
                var delay = string.IsNullOrWhiteSpace(Token) &&
                    string.IsNullOrWhiteSpace(_settings.PluginPairCode)
                    ? CredentiallessRetryDelay
                    : _reconnectDelay;
                var hasNoCredentials = string.IsNullOrWhiteSpace(Token) &&
                    string.IsNullOrWhiteSpace(_settings.PluginPairCode);
                var error = FormatConnectionError(ex);
                UpdateStatus(
                    hasNoCredentials
                        ? CloudConnectionState.WaitingForCredentials
                        : CloudConnectionState.WaitingToRetry,
                    hasNoCredentials
                        ? "等待填写一次性插件配对码"
                        : $"连接失败，将在 {FormatDelay(delay)} 后自动重试",
                    error);
                // 指数退避至上限（生产 5s→10s→…→60s；测试注入毫秒级初始值以便快速验证）。
                _reconnectDelay = TimeSpan.FromTicks(Math.Min(_reconnectDelay.Ticks * 2, MaxReconnectDelay.Ticks));
                try
                {
                    // 设置页的“测试服务器连接”可唤醒退避，不必等待最长两分钟。
                    await _retrySignal.WaitAsync(delay, ct);
                }
                catch (Exception waitEx) when (waitEx is OperationCanceledException or ObjectDisposedException)
                {
                    // 退避等待被取消（插件停止）时静默退出，不能把取消异常留给未观察任务。
                    break;
                }
            }
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Token)) return;
        if (string.IsNullOrWhiteSpace(_settings.PluginPairCode))
            throw new InvalidOperationException("尚未配置一次性插件配对码");

        var response = await _http.PostAsJsonAsync(
            $"{_settings.CloudServerUrl.TrimEnd('/')}/api/plugin/pair",
            new PairRequest { PairCode = _settings.PluginPairCode, Role = "plugin" },
            ct);
        // 409=配对码已用、401=配对码无效、403=端点拒绝（反向代理或权限配置），均视为凭据失效。
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PluginAuthenticationException();
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("插件配对响应为空");
        Token = pair.Token;
        _settings.PluginPairCode = string.Empty;
        _logger.LogInformation("插件云端长期凭据已签发，一次性配对码已清除");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        DisposeSocket();
        _ws = _socketFactory();
        var serverDisplayName = ServerDisplayName();
        // 空闲时由协议层 ping 探测；静默断网会在 ping 超时后让接收循环报错并触发重连。
        _ws.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var builder = new UriBuilder(_settings.CloudServerUrl)
        {
            Path = "/ws",
            Query = $"token={Uri.EscapeDataString(Token ?? string.Empty)}",
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        await _ws.ConnectAsync(builder.Uri, ct);
        if (!await TrySendAsync(Envelope.PeerCapabilities(PluginAppInfo.Capabilities()), ct))
            throw new WebSocketException("连接建立后无法上报插件能力");
        // 每次重连都重新发现网卡，避免 DHCP 或 Wi-Fi 切换后把过期地址留给手表。
        if (!await TrySendAsync(Envelope.PluginNetworkInfo(PluginNetworkInfoProvider.Create(_settings)), ct))
            throw new WebSocketException("连接建立后无法上报插件网络信息");
        _connectedServerDisplayName = serverDisplayName;
        UpdateStatus(CloudConnectionState.Connected, $"已连接 {_connectedServerDisplayName}");
        Connected?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("已连接 RemoteCI 云端：{Host}", builder.Uri.Host);
    }

    /// <summary>
    /// 主动验证当前真实 WebSocket 通道；断线时跳过退避并等待下一次连接尝试的结果。
    /// 不另建旁路 HTTP 探测，避免“网页可打开但插件鉴权/WebSocket 不可用”的误报。
    /// </summary>
    public async Task<CloudConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        await _connectionTestLock.WaitAsync(ct);
        try
        {
            if (!_settings.EnableCloud)
            {
                var disabled = CurrentStatus.State == CloudConnectionState.Disabled
                    ? CurrentStatus
                    : NewStatus(CloudConnectionState.Disabled, "云端连接已在开发者设置中关闭");
                return new CloudConnectionTestResult(false, "测试失败：请先在 RemoteCI 开发者设置中启用云端连接。", disabled);
            }

            if (!_running)
            {
                var stopped = CurrentStatus;
                return new CloudConnectionTestResult(false, "测试失败：RemoteCI 服务尚未启动，请重启 ClassIsland。", stopped);
            }

            if (CurrentStatus.IsConnected)
            {
                if (await TrySendAsync(Envelope.PeerCapabilities(PluginAppInfo.Capabilities()), ct))
                {
                    var connectedServer = _connectedServerDisplayName ?? "当前云端服务器";
                    UpdateStatus(CloudConnectionState.Connected, $"已连接 {connectedServer}（刚刚测试成功）");
                    var configurationChanged = !string.Equals(
                        connectedServer,
                        ServerDisplayName(),
                        StringComparison.OrdinalIgnoreCase);
                    var message = configurationChanged
                        ? $"测试成功：当前 WebSocket 连接 {connectedServer} 可用；新保存的服务器地址将在重启 ClassIsland 后生效。"
                        : "测试成功：服务器 WebSocket 连接可用。";
                    return new CloudConnectionTestResult(true, message, CurrentStatus);
                }

                const string sendError = "现有 WebSocket 已不可用，客户端正在自动重连";
                UpdateStatus(CloudConnectionState.WaitingToRetry, "连接已中断，正在准备重连", sendError);
                RequestImmediateRetry();
                return new CloudConnectionTestResult(false, $"测试失败：{sendError}。", CurrentStatus);
            }

            var completion = new TaskCompletionSource<CloudConnectionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStatusChanged(CloudConnectionStatus status)
            {
                if (status.State is CloudConnectionState.Connected or
                    CloudConnectionState.WaitingForCredentials or
                    CloudConnectionState.WaitingToRetry)
                    completion.TrySetResult(status);
            }

            ConnectionStatusChanged += OnStatusChanged;
            try
            {
                RequestImmediateRetry();
                var status = await completion.Task.WaitAsync(_connectionTestTimeout, ct);
                return status.IsConnected
                    ? new CloudConnectionTestResult(true, "测试成功：已建立并初始化服务器 WebSocket 连接。", status)
                    : new CloudConnectionTestResult(false, $"测试失败：{status.Error ?? status.Summary}。", status);
            }
            catch (TimeoutException)
            {
                return new CloudConnectionTestResult(
                    false,
                    $"测试超时：{FormatDelay(_connectionTestTimeout)} 内未完成连接，请检查地址、端口、HTTPS 证书和内网穿透配置。",
                    CurrentStatus);
            }
            finally
            {
                ConnectionStatusChanged -= OnStatusChanged;
            }
        }
        finally
        {
            _connectionTestLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (_running && _ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                        throw new PluginAuthenticationException();
                    return;
                }
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > VoiceMessageRequest.MaxEnvelopeBytes) throw new InvalidDataException("云端消息过大");
            } while (!result.EndOfMessage);

            await HandleMessageAsync(Encoding.UTF8.GetString(stream.ToArray()), ct);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options);
        if (envelope is null) return;
        if (envelope.ProtocolVersion != Protocol.Version)
            throw new InvalidDataException($"服务端协议为 v{envelope.ProtocolVersion}，插件要求 v{Protocol.Version}");

        if (envelope.Type == Protocol.MessageTypeAccountSync)
        {
            var sync = ConvertPayload<AccountSync>(envelope.Payload);
            if (sync is not null)
            {
                _accounts.Apply(sync);
                _logger.LogInformation("账号、权限和设备会话已同步到版本 {Version}", sync.Version);
            }
            return;
        }

        if (_schedulePullRequests.TryHandle(envelope)) return;

        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        var result = command is null
            ? new CommandResult { Success = false, Code = CommandResultCodes.InvalidRequest, Message = "命令格式无效" }
            : await _handleCommand(command);
        var response = Envelope.CommandResult(result);
        response.ReplyToMessageId = envelope.MessageId;
        await SendAsync(response, ct);
    }

    private async Task SendAsync(Envelope envelope, CancellationToken ct = default) =>
        _ = await TrySendAsync(envelope, ct);

    private async Task<bool> TrySendAsync(Envelope envelope, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open }) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws is not { State: WebSocketState.Open } socket) return false;
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (WebSocketException)
        {
            // 发送失败说明对端已不可达：立即释放连接，让接收循环解除阻塞并触发外层重连。
            DisposeSocket();
            return false;
        }
        catch (ObjectDisposedException)
        {
            // 与 DisposeSocket/Dispose 的正常竞态，无需处理。
            return false;
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { /* 插件正在停止。 */ }
        }
    }

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    /// <summary>凭据失效只由明确的类型信号判定：配对端点 401/403/409 与 WS 的 PolicyViolation。
    /// 不再用异常消息字符串匹配，避免错误文本或 URL 恰好含 “401” 时误清长期凭据。</summary>
    private static bool IsAuthenticationFailure(Exception ex) => ex is PluginAuthenticationException;

    private string FormatConnectionError(Exception ex) => ex switch
    {
        PluginAuthenticationException => "插件配对码或长期凭据无效，请在 WebUI 概览页重新生成配对码",
        InvalidOperationException { Message: "尚未配置一次性插件配对码" } => "尚未配置一次性插件配对码",
        UriFormatException => "云端服务端地址格式无效",
        InvalidDataException => RedactSensitiveText(ex.Message),
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP 请求失败（状态码 {(int)statusCode}）",
        HttpRequestException => $"无法访问服务器：{RedactSensitiveText(ex.InnerException?.Message ?? ex.Message)}",
        WebSocketException webSocket =>
            $"WebSocket 连接失败（{webSocket.WebSocketErrorCode}）：{RedactSensitiveText(webSocket.InnerException?.Message ?? webSocket.Message)}",
        _ => $"{ex.GetType().Name}：{RedactSensitiveText(ex.Message)}",
    };

    private string RedactSensitiveText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未提供详细错误信息，请查看 ClassIsland 日志";
        var redacted = RedactValue(message, Token);
        redacted = RedactValue(redacted, _settings.PluginPairCode);
        var tokenMarker = "token=";
        var markerIndex = redacted.IndexOf(tokenMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return redacted;
        var valueStart = markerIndex + tokenMarker.Length;
        var valueEnd = redacted.IndexOfAny(['&', ' ', '\r', '\n'], valueStart);
        if (valueEnd < 0) valueEnd = redacted.Length;
        return string.Concat(redacted.AsSpan(0, valueStart), "***", redacted.AsSpan(valueEnd));
    }

    private static string RedactValue(string message, string? value) => string.IsNullOrEmpty(value)
        ? message
        : message.Replace(value, "***", StringComparison.Ordinal);

    private string ServerDisplayName()
    {
        var url = _settings.CloudServerUrl?.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? ServerDisplayName(uri)
            : string.IsNullOrWhiteSpace(url) ? "云端服务器" : url;
    }

    private static string ServerDisplayName(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped);

    private static string FormatDelay(TimeSpan delay) => delay.TotalSeconds >= 60
        ? $"{Math.Ceiling(delay.TotalMinutes):0} 分钟"
        : $"{Math.Max(1, Math.Ceiling(delay.TotalSeconds)):0} 秒";

    private static CloudConnectionStatus NewStatus(
        CloudConnectionState state,
        string summary,
        string? error = null) => new(state, summary, error, DateTimeOffset.UtcNow);

    private void UpdateStatus(CloudConnectionState state, string summary, string? error = null)
    {
        var status = NewStatus(state, summary, error);
        Interlocked.Exchange(ref _currentStatus, status);
        try
        {
            ConnectionStatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            // 状态观察者只服务于 UI，绝不能反向打断连接循环。
            _logger.LogDebug(ex, "通知云端连接状态失败");
        }
    }

    private void RequestImmediateRetry()
    {
        if (_retrySignal.CurrentCount > 0) return;
        try { _retrySignal.Release(); }
        catch (SemaphoreFullException) { /* 已有一次待处理的手动重试。 */ }
    }

    private void DisposeSocket()
    {
        try { _ws?.Dispose(); } catch { /* 忽略关闭竞争。 */ }
        _ws = null;
    }

    public void Dispose()
    {
        // 先置位 _running 再取消：RunLoop 各异常分支会检查这两个信号并静默退出，
        // 因此退避等待与发送路径已能容忍 CTS 释放，不再泄漏取消源句柄。
        // _sendLock 仍可能被在途发送持有，留待 GC 终结器回收。
        _running = false;
        UpdateStatus(CloudConnectionState.Stopped, "RemoteCI 服务已停止");
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
        DisposeSocket();
        _http.Dispose();
    }

}

/// <summary>配对端点或 WebSocket 握手明确拒绝当前插件凭据。</summary>
internal sealed class PluginAuthenticationException : Exception
{
    public PluginAuthenticationException()
    {
    }

    public PluginAuthenticationException(Exception innerException)
        : base("插件云端凭据已失效", innerException)
    {
    }
}

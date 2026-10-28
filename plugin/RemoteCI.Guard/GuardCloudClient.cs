using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteCI.Guard;

internal sealed class GuardCloudClient : IDisposable
{
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("RemoteCI.CloudToken.v1");
    private readonly string _directory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _flushing;

    public GuardCloudClient(string directory)
    {
        _directory = directory;
    }

    public Func<GuardCommand, GuardCommandResult>? CommandHandler { get; set; }
    public Action<GuardCommand, GuardCommandResult>? CommandHandled { get; set; }
    public Action<bool, string>? StatusChanged { get; set; }
    public Action<string>? Log { get; set; }

    public void Start() => _ = Task.Run(RunLoopAsync);

    public void NotifyEvent() => _ = FlushEventsAsync(_cts.Token);

    private async Task RunLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var connection = GuardJson.ReadFile<GuardConnectionConfig>(Path.Combine(_directory, GuardFiles.Connection));
                if (connection is not { Enabled: true } || string.IsNullOrWhiteSpace(connection.ServerUrl) ||
                    string.IsNullOrWhiteSpace(connection.ProtectedToken))
                {
                    StatusChanged?.Invoke(false, "等待云端连接配置");
                    await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
                    continue;
                }
                var token = Unprotect(connection.ProtectedToken!);
                if (token is null)
                {
                    StatusChanged?.Invoke(false, "云端凭据无法解密，等待插件刷新");
                    await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
                    continue;
                }
                using var socket = new ClientWebSocket();
                _socket = socket;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var builder = new UriBuilder(connection.ServerUrl.Trim()) { Path = "/ws" };
                builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
                builder.Query = $"token={Uri.EscapeDataString(token)}&client=guard";
                StatusChanged?.Invoke(false, "正在连接服务器");
                await socket.ConnectAsync(builder.Uri, _cts.Token);
                await SendAsync(new GuardEnvelope<GuardPeerCapabilities>
                {
                    Type = "peer_capabilities",
                    Payload = new GuardPeerCapabilities
                    {
                        SoftwareVersion = typeof(GuardCloudClient).Assembly.GetName().Version?.ToString() ?? string.Empty,
                        Capabilities = new List<string> { "host.app-guard", "host.app-guard-launch", "host.app-guard-manage" },
                    },
                }, _cts.Token);
                delay = TimeSpan.FromSeconds(5);
                StatusChanged?.Invoke(true, "守护通道已连接");
                await FlushEventsAsync(_cts.Token);
                await ReceiveLoopAsync(socket, _cts.Token);
                if (!_cts.IsCancellationRequested) throw new WebSocketException("服务器已关闭守护连接");
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                StatusChanged?.Invoke(false, $"连接失败，将自动重试：{ex.Message}");
                Log?.Invoke($"守护云端连接失败：{ex.Message}");
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            }
            finally
            {
                _socket = null;
            }
        }
        StatusChanged?.Invoke(false, "守护通道已停止");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus == WebSocketCloseStatus.PolicyViolation) ClearInvalidToken();
                    return;
                }
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 1024 * 1024) throw new InvalidDataException("云端消息过大");
            } while (!result.EndOfMessage);
            var json = Encoding.UTF8.GetString(stream.ToArray());
            var envelope = GuardJson.Deserialize<GuardEnvelope<Dictionary<string, object>>>(json);
            if (envelope is null || envelope.ProtocolVersion != 3) continue;
            if (envelope.Type == "app_guard_policy")
            {
                var message = GuardJson.Deserialize<GuardEnvelope<GuardPolicyPayload>>(json);
                if (message?.Payload is not null) ApplyPolicy(message.Payload);
            }
            else if (envelope.Type == "command")
            {
                var message = GuardJson.Deserialize<GuardEnvelope<GuardCommand>>(json);
                if (message?.Payload is not null) await HandleCommandAsync(message, ct);
            }
        }
    }

    private void ApplyPolicy(GuardPolicyPayload payload)
    {
        var path = Path.Combine(_directory, GuardFiles.Policy);
        var current = GuardJson.ReadFile<GuardConfig>(path);
        if (current is null) return;
        current.Enabled = payload.Enabled;
        current.EnhancedMode = payload.EnhancedMode;
        current.RestartAfterExit = payload.RestartAfterExit;
        current.RestartDelayMinutes = Math.Max(0, Math.Min(1440, payload.RestartAfterExitDelayMinutes));
        current.RestartOnCrash = payload.RestartOnCrash;
        current.PreventDeletion = payload.PreventDeletion;
        current.BackupIntervalHours = Math.Max(1, Math.Min(168, payload.BackupIntervalHours));
        current.BackupDirectoryName = GuardFiles.NormalizeBackupDirectoryName(payload.BackupDirectoryName);
        GuardBackup.ConfigureProtectedBackup(current, payload.RandomBackupPath, message => Log?.Invoke(message));
        current.ProcessName = GuardFiles.NormalizeProcessName(payload.ProcessName);
        current.BackupPaths = (payload.BackupPaths ?? new List<string>()).Take(3).ToList();
        current.OrganizationName = payload.OrganizationName;
        current.UpdatedAtUnixMs = GuardJson.NowUnixMs();
        GuardJson.WriteFile(path, current);
    }

    private async Task HandleCommandAsync(GuardEnvelope<GuardCommand> message, CancellationToken ct)
    {
        var command = message.Payload!;
        GuardCommandResult result;
        if (command.Command is not (GuardCommands.LaunchClassIsland or GuardCommands.StopAppGuard or GuardCommands.UninstallAppGuard))
            result = GuardCommandResult.Failure("FORBIDDEN", "守护通道仅支持远程打开、关闭或卸载守护");
        else if (command.RequestedBy is not { Role: 2 } || (command.RequestedBy.Permissions & 4) == 0)
            result = GuardCommandResult.Failure("FORBIDDEN", "仅系统管理员可以管理应用守护");
        else if (CommandHandler is null)
            result = GuardCommandResult.Failure("INTERNAL_ERROR", "守护管理功能尚未就绪");
        else
            result = CommandHandler(command);
        await SendAsync(new GuardEnvelope<GuardCommandResult>
        {
            Type = "command_result",
            ReplyToMessageId = message.MessageId,
            Payload = result,
        }, ct);
        CommandHandled?.Invoke(command, result);
    }

    private async Task FlushEventsAsync(CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open } || Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var sending = Path.Combine(_directory, GuardFiles.EventsSending);
            var pending = Path.Combine(_directory, GuardFiles.Events);
            if (!File.Exists(sending))
            {
                if (!File.Exists(pending)) return;
                try { File.Move(pending, sending); }
                catch (IOException) { return; }
            }
            var events = File.ReadAllLines(sending)
                .Select(GuardJson.Deserialize<GuardEventLine>)
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Kind))
                .Select(x => x!)
                .ToList();
            if (events.Count == 0) { File.Delete(sending); return; }
            await SendAsync(new GuardEnvelope<GuardEventBatch>
            {
                Type = "app_guard_event",
                Payload = new GuardEventBatch { Events = events },
            }, ct);
            File.Delete(sending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"守护事件暂未送达：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private async Task SendAsync<T>(GuardEnvelope<T> envelope, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        var bytes = Encoding.UTF8.GetBytes(GuardJson.Serialize(envelope));
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ClearInvalidToken()
    {
        var path = Path.Combine(_directory, GuardFiles.Connection);
        var connection = GuardJson.ReadFile<GuardConnectionConfig>(path);
        if (connection is null) return;
        connection.ProtectedToken = null;
        GuardJson.WriteFile(path, connection);
    }

    private static string? Unprotect(string value)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), TokenEntropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket?.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}

[DataContract]
internal class GuardEnvelope<T>
{
    [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; } = 3;
    [DataMember(Name = "type")] public string Type { get; set; } = string.Empty;
    [DataMember(Name = "messageId")] public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember(Name = "replyToMessageId", EmitDefaultValue = false)] public string? ReplyToMessageId { get; set; }
    [DataMember(Name = "timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    [DataMember(Name = "payload", EmitDefaultValue = false)] public T? Payload { get; set; }
}

[DataContract]
internal sealed class GuardPeerCapabilities
{
    [DataMember(Name = "softwareVersion")] public string SoftwareVersion { get; set; } = string.Empty;
    [DataMember(Name = "capabilities")] public List<string> Capabilities { get; set; } = new();
}

[DataContract]
internal sealed class GuardPolicyPayload
{
    [DataMember(Name = "enabled")] public bool Enabled { get; set; }
    [DataMember(Name = "enhancedMode")] public bool EnhancedMode { get; set; }
    [DataMember(Name = "restartAfterExit")] public bool RestartAfterExit { get; set; }
    [DataMember(Name = "restartAfterExitDelayMinutes")] public int RestartAfterExitDelayMinutes { get; set; }
    [DataMember(Name = "restartOnCrash")] public bool RestartOnCrash { get; set; }
    [DataMember(Name = "preventDeletion")] public bool PreventDeletion { get; set; }
    [DataMember(Name = "backupIntervalHours")] public int BackupIntervalHours { get; set; } = 24;
    [DataMember(Name = "randomBackupPath")] public bool RandomBackupPath { get; set; }
    [DataMember(Name = "backupDirectoryName")] public string BackupDirectoryName { get; set; } = string.Empty;
    [DataMember(Name = "processName")] public string ProcessName { get; set; } = GuardFiles.ProcessName;
    [DataMember(Name = "backupPaths")] public List<string> BackupPaths { get; set; } = new();
    [DataMember(Name = "organizationName")] public string? OrganizationName { get; set; }
}

[DataContract]
internal sealed class GuardCommand
{
    [DataMember(Name = "command")] public int Command { get; set; }
    [DataMember(Name = "requestedBy")] public GuardRequester? RequestedBy { get; set; }
}

[DataContract]
internal sealed class GuardRequester
{
    [DataMember(Name = "role")] public int Role { get; set; }
    [DataMember(Name = "permissions")] public int Permissions { get; set; }
}

[DataContract]
internal sealed class GuardCommandResult
{
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "code")] public string Code { get; set; } = string.Empty;
    [DataMember(Name = "message")] public string Message { get; set; } = string.Empty;

    public static GuardCommandResult Failure(string code, string message) => new() { Success = false, Code = code, Message = message };
    public static GuardCommandResult Ok(string message) => new() { Success = true, Code = "OK", Message = message };
}

[DataContract]
internal sealed class GuardEventBatch
{
    [DataMember(Name = "events")] public List<GuardEventLine> Events { get; set; } = new();
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleck;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class LanServerTests
{
    [Theory]
    [InlineData(UserPermissions.SendVoiceMessages, false, true)]
    [InlineData(UserPermissions.SendNotifications, false, false)]
    [InlineData(UserPermissions.SendVoiceMessages, true, false)]
    public async Task VoiceMessage_LanRequiresIndependentPermissionAndFreshMirror(
        UserPermissions permissions, bool stale, bool allowed)
    {
        var (mirror, sessionId, secret) = CreateFreshMirror(
            stale ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow, permissions);
        CommandMessage? received = null;
        var server = CreateServer(mirror, commandHandler: command =>
        {
            received = command;
            return Task.FromResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        });
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);
        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof, Payload = BuildValidProof(challenge, sessionId, secret),
        }));
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage,
            VoiceMessage = new() { AudioBase64 = Convert.ToBase64String(new byte[VoiceMessageRequest.MaxBytes]) },
            RequestedBy = new UserProfile { DisplayName = "伪造发送人", Permissions = UserPermissions.All },
        });
        await server.OnMessageAsync(socket, SerializeEnvelope(request));
        var reply = ParseEnvelope(socket.Sent[^1]);
        var result = ConvertPayload<CommandResult>(reply.Payload);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.Equal(allowed, result.Success);
        if (allowed)
        {
            Assert.Equal("管理员", received!.RequestedBy!.DisplayName);
            Assert.True(VoiceMessageRequest.TryDecode(received.VoiceMessage, out var audio));
            Assert.Equal(VoiceMessageRequest.MaxBytes, audio.Length);
        }
        else
        {
            Assert.Null(received);
            Assert.Equal(CommandResultCodes.Forbidden, result.Code);
        }
    }

    [Fact]
    public void OnOpened_BootstrapPathSendsCloudBootstrapAndCloses()
    {
        var server = CreateServer();
        var socket = new FakeSocket { Path = "/bootstrap" };

        server.OnOpened(socket);

        Assert.Single(socket.Sent);
        var envelope = ParseEnvelope(socket.Sent[0]);
        Assert.Equal(Protocol.MessageTypeConnectionBootstrap, envelope.Type);
        Assert.Equal(1, socket.CloseCount);
    }

    [Fact]
    public void OnOpened_WebSocketPathSendsFreshChallenge()
    {
        var server = CreateServer();
        var socket = new FakeSocket();

        server.OnOpened(socket);

        Assert.Single(socket.Sent);
        var envelope = ParseEnvelope(socket.Sent[0]);
        Assert.Equal(Protocol.MessageTypeAuthChallenge, envelope.Type);
        var challenge = ConvertPayload<AuthChallenge>(envelope.Payload);
        Assert.False(string.IsNullOrWhiteSpace(challenge.ChallengeId));
        Assert.False(string.IsNullOrWhiteSpace(challenge.Nonce));
        Assert.True(challenge.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Authenticate_InvalidProofRejectsAndCloses()
    {
        var server = CreateServer();
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);

        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = new AuthProof
            {
                ChallengeId = challenge.ChallengeId,
                DeviceSessionId = Guid.NewGuid(),
                ClientNonce = "client-nonce",
                Proof = Convert.ToBase64String(new byte[32]),
            },
        }));

        var last = ParseEnvelope(socket.Sent[^1]);
        Assert.Equal(Protocol.MessageTypeAuthState, last.Type);
        var state = ConvertPayload<AuthState>(last.Payload);
        Assert.False(state.Authenticated);
        Assert.Equal(1, socket.CloseCount);
    }

    [Fact]
    public async Task Authenticate_ValidProofAcceptsAndPushesSnapshot()
    {
        var (mirror, sessionId, secret) = CreateFreshMirror();
        var snapshot = new ClassStateSnapshot { CurrentSubject = "语文" };
        var server = CreateServer(mirror, snapshotProvider: () => snapshot);
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);

        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = BuildValidProof(challenge, sessionId, secret),
        }));

        var types = socket.Sent.Select(message => ParseEnvelope(message).Type).ToList();
        Assert.Contains(Protocol.MessageTypeAuthState, types);
        Assert.Contains(Protocol.MessageTypeCapabilitiesSync, types);
        Assert.Contains(Protocol.MessageTypeStatePush, types);
        var authIndex = types.FindIndex(type => type == Protocol.MessageTypeAuthState);
        var state = ConvertPayload<AuthState>(ParseEnvelope(socket.Sent[authIndex]).Payload);
        Assert.True(state.Authenticated);
        Assert.Equal(UserPermissions.All, state.User?.Permissions);
        var capabilities = ConvertPayload<CapabilitiesSync>(ParseEnvelope(socket.Sent[
            types.FindIndex(type => type == Protocol.MessageTypeCapabilitiesSync)]).Payload);
        Assert.Equal("3.2.1.4", capabilities.Plugin?.SoftwareVersion);
        Assert.Contains(RemoteCiCapabilities.ScheduleChange, capabilities.Server.Capabilities);
    }

    [Fact]
    public async Task Authenticate_ValidProofPushesExtensionsRegisteredBeforeConnection()
    {
        var (mirror, sessionId, secret) = CreateFreshMirror();
        var server = CreateServer(mirror);
        server.BroadcastExtensions(
        [
            new ExtensionDefinition
            {
                Id = "demo.registered",
                DisplayName = "已注册扩展",
                RequiredPermission = UserPermissions.ViewCurrentCourse,
            },
        ]);
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);

        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = BuildValidProof(challenge, sessionId, secret),
        }));

        var pushed = socket.Sent
            .Select(ParseEnvelope)
            .Single(envelope => envelope.Type == Protocol.MessageTypeExtensionsSync);
        var extension = Assert.Single(ConvertPayload<List<ExtensionDefinition>>(pushed.Payload));
        Assert.Equal("demo.registered", extension.Id);
        Assert.Equal("已注册扩展", extension.DisplayName);
    }

    [Fact]
    public async Task Command_ExpiredMirrorIsDeniedWithoutInvokingHandler()
    {
        var (mirror, sessionId, secret) = CreateFreshMirror(generatedAt: DateTimeOffset.UtcNow.AddHours(-25));
        var handlerCalls = 0;
        var server = CreateServer(mirror, commandHandler: _ =>
        {
            handlerCalls++;
            return Task.FromResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        });
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);
        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = BuildValidProof(challenge, sessionId, secret),
        }));

        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeCommand,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = new CommandMessage
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest { Title = "通知", Message = "正文" },
            },
        }));

        var result = ConvertPayload<CommandResult>(ParseEnvelope(socket.Sent[^1]).Payload);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
        Assert.Contains("24 小时", result.Message);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task OnClosed_DropsQueuedMessagesWithoutWaitingOrThrowing()
    {
        var (mirror, sessionId, secret) = CreateFreshMirror();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerGate = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(mirror, commandHandler: async _ =>
        {
            handlerEntered.TrySetResult();
            return await handlerGate.Task;
        });
        var socket = new FakeSocket();
        server.OnOpened(socket);
        var challenge = ConvertPayload<AuthChallenge>(ParseEnvelope(socket.Sent[0]).Payload);
        await server.OnMessageAsync(socket, SerializeEnvelope(new Envelope
        {
            Type = Protocol.MessageTypeAuthProof,
            MessageId = Guid.NewGuid().ToString("N"),
            Payload = BuildValidProof(challenge, sessionId, secret),
        }));

        // 第一条命令占住 Gate 执行中，第二条消息排队等待；此时断开连接：
        // 排队消息必须被取消丢弃，不能等第一条执行完，更不能抛 Gate 已释放异常。
        var firstCommand = server.OnMessageAsync(socket, SerializeCommand("cmd-1"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = server.OnMessageAsync(socket, SerializeCommand("cmd-2"));

        server.OnClosed(socket);
        await queued.WaitAsync(TimeSpan.FromSeconds(5));

        handlerGate.SetResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        await firstCommand.WaitAsync(TimeSpan.FromSeconds(5));

        // 已关闭连接上的后续消息静默忽略，不产生任何回执。
        var sentBefore = socket.Sent.Count;
        await server.OnMessageAsync(socket, SerializeCommand("cmd-3"));
        Assert.Equal(sentBefore, socket.Sent.Count);
    }

    [Fact]
    public async Task SemaphoreGateWait_WokenUpByCancelAlone()
    {
        var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var cts = new CancellationTokenSource();
        var wait = gate.WaitAsync(cts.Token);
        // 与 LanClient.Dispose 一致的唤醒方式：仅 Cancel。
        // 已实证 Cancel 后紧跟 Dispose 该 CTS 或 Gate 都会丢失排队等待者的唤醒通知。
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static string SerializeCommand(string messageId) => SerializeEnvelope(new Envelope
    {
        Type = Protocol.MessageTypeCommand,
        MessageId = messageId,
        Payload = new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest { Title = "通知", Message = "正文" },
        },
    });

    private static LanServer CreateServer(
        AccountMirror? mirror = null,
        Func<ClassStateSnapshot?>? snapshotProvider = null,
        Func<CommandMessage, Task<CommandResult>>? commandHandler = null) => new(
        new PluginSettings { CloudServerUrl = "https://cloud.example.com" },
        mirror ?? new AccountMirror(TempPath("accounts.json")),
        commands: null,
        requestScheduleSync: request => new ScheduleSyncStatus
        {
            TaskId = request.TaskId,
            Source = request.Source,
            State = ScheduleSyncTaskState.Running,
        },
        snapshotProvider: snapshotProvider ?? (() => null),
        scheduleProvider: () => null,
        NullLogger<LanServer>.Instance,
        commandHandler: commandHandler);

    private static (AccountMirror Mirror, Guid SessionId, string Secret) CreateFreshMirror(
        DateTimeOffset? generatedAt = null, UserPermissions permissions = UserPermissions.All)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var mirror = new AccountMirror(TempPath("accounts.json"));
        mirror.Apply(new AccountSync
        {
            Version = 1,
            ServerVersion = "0.3.1",
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow,
            Accounts = [new SyncedAccount
            {
                Id = userId,
                Username = "admin",
                DisplayName = "管理员",
                Role = permissions == UserPermissions.All ? UserRole.Admin : UserRole.User,
                EffectivePermissions = permissions | UserPermissions.ViewCurrentCourse,
                Enabled = true,
                Version = 1,
            }],
            Sessions = [new SyncedDeviceSession
            {
                Id = sessionId,
                UserId = userId,
                Verifier = Convert.ToHexString(verifier).ToLowerInvariant(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            }],
        });
        return (mirror, sessionId, secret);
    }

    private static AuthProof BuildValidProof(AuthChallenge challenge, Guid sessionId, string secret)
    {
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        const string clientNonce = "client-nonce";
        var proof = Convert.ToBase64String(HMACSHA256.HashData(
            verifier,
            Encoding.UTF8.GetBytes(AccountMirror.CanonicalProof(challenge, sessionId, clientNonce))));
        return new AuthProof
        {
            ChallengeId = challenge.ChallengeId,
            DeviceSessionId = sessionId,
            ClientNonce = clientNonce,
            Proof = proof,
        };
    }

    private static string TempPath(string name) => Path.Combine(
        Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), name);

    private static Envelope ParseEnvelope(string json) =>
        JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;

    private static string SerializeEnvelope(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, JsonDefaults.Options);

    private static T ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options)!;

    private sealed class FakeSocket : IWebSocketConnection
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Path { get; set; } = "/ws";
        public bool IsAvailable { get; set; } = true;
        public List<string> Sent { get; } = [];
        public int CloseCount { get; private set; }
        public IWebSocketConnectionInfo ConnectionInfo => new FakeConnectionInfo(Id, Path);
        public Action? OnOpen { get; set; }
        public Action? OnClose { get; set; }
        public Action<string>? OnMessage { get; set; }
        public Action<byte[]>? OnBinary { get; set; }
        public Action<byte[]>? OnPing { get; set; }
        public Action<byte[]>? OnPong { get; set; }
        public Action<Exception>? OnError { get; set; }

        public Task Send(string message)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(byte[] message) => Task.CompletedTask;
        public Task SendPing(byte[] message) => Task.CompletedTask;
        public Task SendPong(byte[] message) => Task.CompletedTask;
        public void Close() { CloseCount++; IsAvailable = false; }
        public void Close(int code) => Close();
    }

    private sealed class FakeConnectionInfo(Guid id, string path) : IWebSocketConnectionInfo
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string Host => string.Empty;
        public string Origin => string.Empty;
        public string SubProtocol => string.Empty;
        public string NegotiatedSubProtocol => string.Empty;
        public string ClientIpAddress => "127.0.0.1";
        public int ClientPort => 0;
        public IDictionary<string, string> Cookies { get; } = new Dictionary<string, string>();
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    }
}

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class WebSocketRelayTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WebSocketRelayTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task VoiceMessage_RelaysFullMinuteWithAuthenticatedSenderAndCorrelatedReply()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>().PrimaryPluginSupports(RemoteCiCapabilities.VoiceMessageSend));
        using var watch = await ConnectWatchAsync();
        var audio = new byte[VoiceMessageRequest.MaxBytes];
        new Random(42).NextBytes(audio);
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage,
            VoiceMessage = new() { AudioBase64 = Convert.ToBase64String(audio) },
            RequestedBy = new UserProfile { DisplayName = "伪造发送人", Permissions = UserPermissions.All },
        });
        await SendAsync(watch, request);
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.True(VoiceMessageRequest.TryDecode(command.VoiceMessage, out var received));
        Assert.Equal(audio, received);
        Assert.NotEqual("伪造发送人", command.RequestedBy!.DisplayName);
        Assert.Equal(TestWebApplicationFactory.AdminUsername, command.RequestedBy.Username);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult, ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok },
        });
        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.True(ConvertPayload<CommandResult>(reply.Payload).Success);
    }

    [Fact]
    public async Task MobileCommand_RelaysToPluginAndReturnsCorrelatedReply()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        using var mobile = await ConnectMobileAsync();
        await ReceivePayloadAsync<AuthState>(mobile, Protocol.MessageTypeAuthState);
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.TeacherComing,
        });

        await SendAsync(mobile, request);

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        Assert.Equal(CommandKind.TeacherComing, ConvertPayload<CommandMessage>(forwarded.Payload).Command);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok },
        });
        var reply = await ReceiveEnvelopeAsync(mobile, Protocol.MessageTypeCommandResult);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.True(ConvertPayload<CommandResult>(reply.Payload).Success);
        Assert.Equal(1, _factory.Services.GetRequiredService<PeerRegistry>().MobileCount);
    }

    [Fact]
    public async Task VoiceMessage_RejectsMalformedAudioBeforeForwarding()
    {
        using var watch = await ConnectWatchAsync();
        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage, VoiceMessage = new() { AudioBase64 = "AAA" },
        }));
        var result = await ReceivePayloadAsync<CommandResult>(watch, Protocol.MessageTypeCommandResult);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task WatchAuthentication_ReportsConnectedServerVersion()
    {
        using var watch = await ConnectWatchAsync();

        var auth = await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);

        Assert.True(auth.Authenticated);
        Assert.Equal(AppVersion.Version, auth.ServerVersion);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task WatchMessages_FromOtherProtocolVersionsAreRejected(int protocolVersion)
    {
        using var watch = await ConnectWatchAsync();
        await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);

        var incompatible = Envelope.SchedulePull();
        incompatible.ProtocolVersion = protocolVersion;
        await SendAsync(watch, incompatible);

        var error = await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);
        Assert.False(error.Authenticated);
        Assert.Equal(ApiErrorCodes.ProtocolVersionUnsupported, error.ErrorCode);
    }

    [Fact]
    public async Task PluginStringProtocolMismatchIsRecordedUntilCompatiblePluginCommunicates()
    {
        using var incompatiblePlugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(incompatiblePlugin, Protocol.MessageTypeSchedulePull);
        var json = JsonSerializer.Serialize(Envelope.SchedulePull(), JsonDefaults.Options)
            .Replace("\"protocolVersion\":3", "\"protocolVersion\":\"3.1\"", StringComparison.Ordinal);
        await SendTextAsync(incompatiblePlugin, json);

        var error = await ReceivePayloadAsync<AuthState>(
            incompatiblePlugin, Protocol.MessageTypeAuthState);
        Assert.Equal(ApiErrorCodes.ProtocolVersionUnsupported, error.ErrorCode);
        Assert.Equal("3.1", _factory.Services.GetRequiredService<PeerRegistry>()
            .LatestPluginProtocolMismatch?.ActualVersion);

        using var compatiblePlugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(compatiblePlugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(compatiblePlugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.1.0",
            Capabilities = RemoteCiCapabilities.Baseline,
        }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>()
            .LatestPluginProtocolMismatch is null);
    }

    [Fact]
    public async Task Capabilities_AreSynchronizedAndUnsupportedCommandIsRejected()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        using var watch = await ConnectWatchAsync();
        var fallbackSync = await ReceivePayloadAsync<CapabilitiesSync>(
            watch, Protocol.MessageTypeCapabilitiesSync);
        Assert.Equal(RemoteCiCapabilities.Baseline, fallbackSync.Plugin?.Capabilities);

        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.9.4",
            Capabilities = [RemoteCiCapabilities.ScheduleRead, "future.experimental"],
        }));
        var sync = await ReceivePayloadAsync<CapabilitiesSync>(watch, Protocol.MessageTypeCapabilitiesSync);
        Assert.Equal("3.9.4", sync.Plugin?.SoftwareVersion);
        Assert.Contains(RemoteCiCapabilities.ScheduleRead, sync.Plugin!.Capabilities);
        Assert.Contains("future.experimental", sync.Plugin.Capabilities);

        await SendAsync(watch, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.1.0",
            Capabilities = [RemoteCiCapabilities.ScheduleRead],
        }));
        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.Volume,
            Volume = new VolumeControlRequest { Level = 50 },
        }));
        var result = await ReceivePayloadAsync<CommandResult>(watch, Protocol.MessageTypeCommandResult);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.CapabilityUnsupported, result.Code);

        var diagnostics = _factory.Services.GetRequiredService<PeerRegistry>().GetCapabilityDiagnostics();
        var watchDiagnostic = Assert.Single(diagnostics, item => item.Role == PeerRole.Watch);
        Assert.True(watchDiagnostic.IsExplicit);
        Assert.Equal("3.1.0", watchDiagnostic.SoftwareVersion);
        Assert.Contains(RemoteCiCapabilities.VolumeControl, watchDiagnostic.MissingCapabilities);
        var pluginDiagnostic = Assert.Single(diagnostics, item => item.Role == PeerRole.Plugin);
        Assert.DoesNotContain("future.experimental", pluginDiagnostic.EffectiveCapabilities);
    }

    [Fact]
    public async Task PluginConnection_ImmediatelyRequestsFreshSchedule()
    {
        using var plugin = await ConnectPluginAsync();

        var request = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);

        Assert.Equal(Protocol.Version, request.ProtocolVersion);
    }

    [Fact]
    public async Task RequestIsDeliveredToOnlyOnePluginWhenMultipleConnected()
    {
        using var pluginA = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(pluginA, Protocol.MessageTypeSchedulePull); // 连接后的初始拉取。
        using var pluginB = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(pluginB, Protocol.MessageTypeSchedulePull);
        using var watch = await ConnectWatchAsync();

        await SendAsync(watch, Envelope.SchedulePull());

        // 最早接入的插件 A 收到请求；插件 B 在接收超时内不应收到（避免多插件重复执行）。
        var receiveA = ReceiveEnvelopeAsync(pluginA, Protocol.MessageTypeSchedulePull);
        var receiveB = ReceiveEnvelopeAsync(pluginB, Protocol.MessageTypeSchedulePull);
        var winner = await Task.WhenAny(receiveA, receiveB);
        Assert.Equal(Protocol.MessageTypeSchedulePull, (await winner).Type);
        var loser = ReferenceEquals(winner, receiveA) ? receiveB : receiveA;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loser);
    }

    [Fact]
    public async Task AuthenticatedWatchSchedulePull_IsForwardedWithoutScheduleManagementPermission()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "schedule.reader",
                DisplayName = "课表查看者",
                Password = "Schedule-Reader-Password-2026",
            }));
        create.EnsureSuccessStatusCode();
        using var watch = await ConnectWatchAsync("schedule.reader", "Schedule-Reader-Password-2026");

        await SendAsync(watch, Envelope.SchedulePull());

        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
    }

    [Fact]
    public async Task PluginPushesCurrentStateAndSevenDaySchedule_AllWatchesReceiveBoth()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        await SendAsync(plugin, Envelope.StatePush(new ClassStateSnapshot
        {
            CurrentSubject = "语文",
            CurrentState = ClassStateKind.Class,
            IsClassPlanLoaded = true,
        }));
        await SendAsync(plugin, Envelope.ScheduleSync(new ScheduleBundle
        {
            FromDate = "2026-08-11",
            Days = [new ScheduleDay
            {
                Date = "2026-08-11",
                Revision = "revision-a",
                Enabled = true,
                Courses = [new CourseEntry { Index = 0, Label = "第 1 节", SubjectId = Guid.NewGuid(), Subject = "语文" }],
            }],
        }));

        var state = await ReceivePayloadAsync<ClassStateSnapshot>(watch, Protocol.MessageTypeStatePush);
        var schedule = await ReceivePayloadAsync<ScheduleBundle>(watch, Protocol.MessageTypeScheduleSync);
        Assert.Equal("语文", state.CurrentSubject);
        Assert.Equal("revision-a", schedule.Days.Single().Revision);
        Assert.Single(schedule.Days.Single().Courses);
    }

    [Fact]
    public async Task PluginNetworkInfo_IsRelayedAndCachedForWatches()
    {
        using var plugin = await ConnectPluginAsync();
        using var connectedWatch = await ConnectWatchAsync();
        var expected = new PluginNetworkInfo
        {
            LanServerEnabled = true,
            Port = 9876,
            Addresses = ["192.168.50.8", "10.0.0.8"],
        };

        await SendAsync(plugin, Envelope.PluginNetworkInfo(expected));

        var relayed = await ReceivePayloadAsync<PluginNetworkInfo>(
            connectedWatch, Protocol.MessageTypePluginNetworkInfo);
        Assert.Equal(expected.Port, relayed.Port);
        Assert.Equal(expected.Addresses, relayed.Addresses);

        using var laterWatch = await ConnectWatchAsync();
        var cached = await ReceivePayloadAsync<PluginNetworkInfo>(
            laterWatch, Protocol.MessageTypePluginNetworkInfo);
        Assert.Equal(expected.Port, cached.Port);
        Assert.Equal(expected.Addresses, cached.Addresses);
    }

    [Fact]
    public async Task AdminCommand_IsForwardedAndResultReturnsOnlyByCorrelationId()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.ChangeSchedule,
            ScheduleChange = new ScheduleChangeRequest
            {
                Date = "2026-08-11",
                Mode = ScheduleChangeMode.Exchange,
                SourceIndex = 0,
                TargetIndex = 1,
                ExpectedRevision = "revision-a",
            },
        });
        await SendAsync(watch, request);

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.ChangeSchedule, command.Command);
        Assert.Equal(UserRole.Admin, command.RequestedBy?.Role);

        var result = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已换课" };
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = result,
        });

        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        var received = ConvertPayload<CommandResult>(reply.Payload);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.True(received.Success);
        Assert.Equal("已换课", received.Message);
    }

    [Fact]
    public async Task OrdinaryUserCommand_IsRejectedBeforePluginExecution()
    {
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "ws.student",
                DisplayName = "WebSocket 学生",
                Password = "Student-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        using var watch = await ConnectWatchAsync("ws.student", "Student-Password-2026");
        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest { Title = "x", Message = "x" },
        }));
        var result = await ReceivePayloadAsync<CommandResult>(watch, Protocol.MessageTypeCommandResult);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task PermissionChanges_PushVersionedPasswordFreeMirrorToPlugin()
    {
        using var plugin = await ConnectPluginAsync();
        var initialSync = await ReceivePayloadAsync<AccountSync>(plugin, Protocol.MessageTypeAccountSync);
        Assert.Equal(AppVersion.Version, initialSync.ServerVersion);
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "sync.student",
                DisplayName = "同步学生",
                Password = "Sync-Student-Password-2026",
                GrantedPermissions = UserPermissions.SendNotifications,
            }));
        create.EnsureSuccessStatusCode();

        // 登录管理员也会创建新设备会话并推送一个镜像，因此按账号版本等待创建用户后的镜像。
        AccountSync sync;
        do
        {
            sync = await ReceivePayloadAsync<AccountSync>(plugin, Protocol.MessageTypeAccountSync);
        }
        while (sync.Version <= initialSync.Version || sync.Accounts.All(x => x.Username != "sync.student"));
        var account = Assert.Single(sync.Accounts, x => x.Username == "sync.student");
        Assert.Equal(UserPermissions.ViewCurrentCourse | UserPermissions.SendNotifications, account.EffectivePermissions);
        var json = JsonSerializer.Serialize(sync, JsonDefaults.Options);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(sync.Version > 0);
    }

    [Fact]
    public async Task PluginPushesExtensions_AllWatchesReceiveThem()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        await SendAsync(plugin, Envelope.ExtensionsSync(new List<ExtensionDefinition>
        {
            new ExtensionDefinition
            {
                Id = "demo.lock",
                DisplayName = "锁屏",
                Icon = "power",
                RequiredPermission = UserPermissions.SystemControl,
            },
        }));

        var received = await ReceivePayloadAsync<List<ExtensionDefinition>>(watch, Protocol.MessageTypeExtensionsSync);
        var extension = Assert.Single(received);
        Assert.Equal("demo.lock", extension.Id);
        Assert.Equal("锁屏", extension.DisplayName);
        Assert.Equal(UserPermissions.SystemControl, extension.RequiredPermission);
    }

    [Fact]
    public async Task NewWatchConnection_ReceivesCachedExtensions()
    {
        using var plugin = await ConnectPluginAsync();
        await SendAsync(plugin, Envelope.ExtensionsSync(new List<ExtensionDefinition>
        {
            new ExtensionDefinition
            {
                Id = "demo.lock",
                DisplayName = "锁屏",
                RequiredPermission = UserPermissions.SystemControl,
            },
        }));

        using var watch = await ConnectWatchAsync();
        var received = await ReceivePayloadAsync<List<ExtensionDefinition>>(watch, Protocol.MessageTypeExtensionsSync);
        Assert.Equal("demo.lock", Assert.Single(received).Id);
    }

    [Fact]
    public async Task ExtensionCommand_IsForwardedToPluginAndResultReturns()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        await SendAsync(plugin, Envelope.ExtensionsSync(new List<ExtensionDefinition>
        {
            new()
            {
                Id = "demo.lock",
                DisplayName = "锁屏",
                RequiredPermission = UserPermissions.PowerControl,
            },
        }));
        await ReceivePayloadAsync<List<ExtensionDefinition>>(watch, Protocol.MessageTypeExtensionsSync);
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.lock",
            ExtensionArgs = new Dictionary<string, string?> { ["message"] = "下课了" },
        });
        await SendAsync(watch, request);

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.RunExtension, command.Command);
        Assert.Equal("demo.lock", command.ExtensionId);
        Assert.Equal("下课了", command.ExtensionArgs!["message"]);
        Assert.Equal(UserRole.Admin, command.RequestedBy?.Role);

        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已执行" },
        });

        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
    }

    [Fact]
    public async Task WatchReceivesDefaultSettings_AndNotificationCommandGetsServerPolicyInjected()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();

        var settings = await ReceivePayloadAsync<SettingsSync>(watch, Protocol.MessageTypeSettingsSync);
        Assert.True(settings.ForceSenderInTitle);

        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest { Title = "x", Message = "x" },
        }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.True(command.Notification?.ForceSenderInTitle);
    }

    [Fact]
    public async Task PluginStatePush_DoesNotRevalidateCredentialForEveryMessage()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "ws-query-count.db");
        var commands = new DatabaseCommandCounterLoggerProvider();
        await using var factory = TestWebApplicationFactory.ForDatabaseAndLogger(databasePath, commands);
        var token = await factory.GetPluginTokenAsync();
        var socketClient = factory.Server.CreateWebSocketClient();
        using var plugin = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"),
            CancellationToken.None);
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);

        commands.Reset();
        for (var index = 0; index < 3; index++)
        {
            await SendAsync(plugin, Envelope.StatePush(new ClassStateSnapshot
            {
                CurrentSubject = $"性能回归-{index}",
                CurrentState = ClassStateKind.Class,
            }));
        }

        var store = factory.Services.GetRequiredService<IStateStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (store.GetLatestSnapshot()?.CurrentSubject != "性能回归-2")
            await Task.Delay(10, timeout.Token);

        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public async Task PluginStatePush_ToConnectedWatchDoesNotRevalidateEitherPeer()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "ws-broadcast-query-count.db");
        var commands = new DatabaseCommandCounterLoggerProvider();
        await using var factory = TestWebApplicationFactory.ForDatabaseAndLogger(databasePath, commands);
        var pluginToken = await factory.GetPluginTokenAsync();
        var watchToken = (await factory.LoginAsync()).AccessToken;
        var socketClient = factory.Server.CreateWebSocketClient();
        using var plugin = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None);
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        using var watch = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(watchToken)}"),
            CancellationToken.None);
        await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeSettingsSync);

        commands.Reset();
        await SendAsync(plugin, Envelope.StatePush(new ClassStateSnapshot
        {
            CurrentSubject = "广播性能回归",
            CurrentState = ClassStateKind.Class,
        }));
        var received = await ReceivePayloadAsync<ClassStateSnapshot>(watch, Protocol.MessageTypeStatePush);

        Assert.Equal("广播性能回归", received.CurrentSubject);
        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public async Task AuthorizationFallback_DisconnectsIdlePluginRevokedOutsideTheApi()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "ws-authorization-fallback.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath, new Dictionary<string, string?>
        {
            ["Server:ConnectionAuthorizationRefreshInterval"] = "00:00:00.100",
        });
        var token = await factory.GetPluginTokenAsync();
        var socketClient = factory.Server.CreateWebSocketClient();
        using var plugin = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"),
            CancellationToken.None);
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var credential = await database.PluginCredentials.SingleAsync();
            credential.Enabled = false;
            await database.SaveChangesAsync();
        }

        await AssertWebSocketClosedAsync(plugin);
    }

    [Fact]
    public async Task WatchAccessTokenExpiry_ClosesFromCachedExpiryWithoutDatabaseRevalidation()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "ws-token-expiry.db");
        var commands = new DatabaseCommandCounterLoggerProvider();
        await using var factory = TestWebApplicationFactory.ForDatabaseAndLogger(
            databasePath,
            commands,
            new Dictionary<string, string?>
            {
                ["Server:AccessTokenTtl"] = "00:00:02",
                ["Server:ConnectionAuthorizationRefreshInterval"] = "00:01:00",
            });
        var auth = await factory.LoginAsync();
        var socketClient = factory.Server.CreateWebSocketClient();
        using var watch = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(auth.AccessToken)}"),
            CancellationToken.None);
        await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeSettingsSync);

        commands.Reset();
        var remaining = auth.AccessExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await SendAsync(watch, Envelope.SchedulePull());

        await AssertWebSocketClosedAsync(watch);
        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public async Task WatchAuthorization_RefreshesCachedPermissionsAfterAdminUpdate()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "ws-permission-refresh.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "ws.permission.user",
                DisplayName = "权限刷新用户",
                Password = "Permission-Refresh-Password-2026",
            }));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<UserListItem>())!;
        var watchAuth = await factory.LoginAsync("ws.permission.user", "Permission-Refresh-Password-2026");
        var socketClient = factory.Server.CreateWebSocketClient();
        using var watch = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(watchAuth.AccessToken)}"),
            CancellationToken.None);
        var initial = await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);
        Assert.False(initial.User!.Permissions.HasFlag(UserPermissions.SendNotifications));

        var update = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{created.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = created.DisplayName,
                Role = UserRole.User,
                Enabled = true,
                GrantedPermissions = UserPermissions.SendNotifications,
            }));
        update.EnsureSuccessStatusCode();

        var refreshed = await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);
        Assert.True(refreshed.User!.Permissions.HasFlag(UserPermissions.SendNotifications));
    }

    private async Task<WebSocket> ConnectPluginAsync() => await ConnectAsync(await _factory.GetPluginTokenAsync());

    private async Task<WebSocket> ConnectWatchAsync(
        string username = TestWebApplicationFactory.AdminUsername,
        string password = TestWebApplicationFactory.AdminPassword) =>
        await ConnectAsync((await _factory.LoginAsync(username, password)).AccessToken);

    private async Task<WebSocket> ConnectMobileAsync() =>
        await ConnectAsync((await _factory.LoginAsync()).AccessToken, "mobile");

    private async Task<WebSocket> ConnectAsync(string token, string? clientKind = null)
    {
        var socketClient = _factory.Server.CreateWebSocketClient();
        var clientQuery = string.IsNullOrWhiteSpace(clientKind) ? string.Empty : $"&client={Uri.EscapeDataString(clientKind)}";
        return await socketClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}{clientQuery}"),
            CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendTextAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static async Task AssertWebSocketClosedAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType != WebSocketMessageType.Close) continue;
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
            return;
        }
    }

    private static async Task<T> ReceivePayloadAsync<T>(WebSocket socket, string expectedType) =>
        ConvertPayload<T>((await ReceiveEnvelopeAsync(socket, expectedType)).Payload);

    private static async Task<Envelope> ReceiveEnvelopeAsync(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var json = Encoding.UTF8.GetString(message.ToArray());
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
            if (envelope.Type == expectedType) return envelope;
        }
    }

    private static T ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options)!;
}

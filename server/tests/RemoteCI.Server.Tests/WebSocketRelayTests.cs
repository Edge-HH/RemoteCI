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

/// <remarks>
/// 本类所有测试共享同一个服务实例。上一个测试释放的插件连接由服务端异步注销，注销前它仍可能被选为
/// “主插件”而截走下一个测试的命令，导致全量运行与单独运行结果不一致。因此每个测试结束时主动关闭
/// 自己打开的连接，并等待注册表清空后才进入下一个测试。
/// </remarks>
public sealed class WebSocketRelayTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly List<WebSocket> _sockets = [];

    public WebSocketRelayTests(TestWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => WaitForNoPluginAsync();

    public async Task DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test finished", timeout.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 测试本身已经关闭或中止的连接无需再处理。
            }
            socket.Dispose();
        }
        await WaitForNoPluginAsync();
    }

    private Task WaitForNoPluginAsync() => WaitUntilAsync(() =>
        !_factory.Services.GetRequiredService<PeerRegistry>().HasPluginFor(Classroom.DefaultId));

    [Fact]
    public async Task VoiceMessage_RelaysFullMinuteWithAuthenticatedSenderAndCorrelatedReply()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>().PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.VoiceMessageSend));
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
    public async Task SoftwareInventory_IsCachedPersistedAndReturnedForDevicePage()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.2.1.4",
            Capabilities = RemoteCiCapabilities.Current,
        }));
        await SendAsync(plugin, Envelope.SoftwareInventory(new SoftwareInventory
        {
            DeviceName = "教室电脑-A",
            OperatingSystem = "Windows",
            Architecture = "X64",
            Applications =
            [
                new SoftwarePackageInfo
                {
                    Id = "classisland",
                    Name = "ClassIsland",
                    Version = "2.1.1.1",
                    LatestVersion = "2.1.2.0",
                    IsUpdateAvailable = true,
                    CanUpgrade = true,
                },
            ],
            Plugins =
            [
                new SoftwarePackageInfo
                {
                    Id = "remoteci.plugin",
                    Name = "RemoteCI",
                    Version = "3.2.1.4",
                    LatestVersion = "3.2.1.4",
                    CanUpgrade = true,
                },
            ],
            LastUpdate = new SoftwareUpdateStatus
            {
                Operation = SoftwareUpdateOperation.InventoryRefresh,
                State = SoftwareUpdateState.Completed,
                Message = "版本清单已刷新",
            },
        }));

        var registry = _factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => registry.GetPluginDeviceSnapshots().Any(
            item => item.SoftwareInventory?.DeviceName == "教室电脑-A"));
        var snapshot = Assert.Single(registry.GetPluginDeviceSnapshots());
        Assert.Equal("3.2.1.4", snapshot.SoftwareVersion);
        Assert.Contains(RemoteCiCapabilities.SoftwareInventory, snapshot.EffectiveCapabilities);
        Assert.NotNull(snapshot.PluginCredentialId);
        var credentialId = snapshot.PluginCredentialId!.Value;
        await WaitUntilAsync(() =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return db.PluginCredentials.AsNoTracking().Any(item =>
                item.Id == credentialId && item.SoftwareInventoryJson != null);
        });
        using var persistedScope = _factory.Services.CreateScope();
        var persistedDb = persistedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = await persistedDb.PluginCredentials.AsNoTracking()
            .SingleAsync(item => item.Id == credentialId);
        using var inventoryJson = JsonDocument.Parse(credential.SoftwareInventoryJson!);
        Assert.Equal("教室电脑-A", inventoryJson.RootElement.GetProperty("deviceName").GetString());
    }

    [Fact]
    public async Task DirectCommand_IsSentToSelectedPluginConnection()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.2.1.4",
            Capabilities = RemoteCiCapabilities.Current,
        }));

        var registry = _factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => registry.GetPluginDeviceSnapshots().Count == 1);
        var target = registry.GetPluginDeviceSnapshots().Single();
        var pending = registry.SendCommandAndWaitToConnectionAsync(new CommandMessage
        {
            Command = CommandKind.RefreshSoftwareInventory,
        }, target.ConnectionId, TimeSpan.FromSeconds(5));

        var commandEnvelope = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        Assert.Equal(CommandKind.RefreshSoftwareInventory,
            ConvertPayload<CommandMessage>(commandEnvelope.Payload).Command);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = commandEnvelope.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "ok" },
        });

        var result = await pending;
        Assert.True(result.Success);
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
        // 班级配对码只允许一台设备，新连接会让旧连接失效。
        await AssertWebSocketClosedAsync(pluginA);
        using var watch = await ConnectWatchAsync();

        await SendAsync(watch, Envelope.SchedulePull());
        await ReceiveEnvelopeAsync(pluginB, Protocol.MessageTypeSchedulePull);
    }

    [Fact]
    public async Task ClassAdministratorWatchSchedulePull_IsForwarded()
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
                DisplayName = "班级管理员",
                Password = "Schedule-Reader-Password-2026",
                RoleId = AccountRole.ClassAdministratorId,
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
    public async Task ExtensionGroups_SyncedFromPlugin_AdminAppliesPartialSettingsThroughRest()
    {
        using var plugin = await ConnectPluginAsync();
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>()
            .PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ExtensionsSettings));
        await SendAsync(plugin, Envelope.ExtensionGroupsSync(new List<ExtensionGroupDefinition>
        {
            new()
            {
                Id = "demo.settings",
                DisplayName = "演示插件",
                Settings =
                [
                    new ExtensionParameter { Key = "volume", Label = "音量", Type = ExtensionParameterType.Number, Min = 0, Max = 100 },
                    new ExtensionParameter
                    {
                        Key = "mode", Label = "模式", Type = ExtensionParameterType.Select,
                        Options = ["a", "b"], OptionLabels = ["模式 A", "模式 B"],
                    },
                ],
                Values = new Dictionary<string, string?> { ["volume"] = "50", ["mode"] = "a" },
            },
        }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<IStateStore>()
            .GetLatestExtensionGroups(Classroom.DefaultId)?.Any(x => x.Id == "demo.settings") == true);

        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var listed = await (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/extension-groups", admin.AccessToken))).Content.ReadFromJsonAsync<JsonElement>();
        var group = listed.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "demo.settings");
        Assert.True(group.GetProperty("canEditSettings").GetBoolean());
        Assert.Equal("50", group.GetProperty("values").GetProperty("volume").GetString());

        // 服务端按插件声明预校验，非法值不会下发到设备。
        var invalid = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, $"/api/classes/{Classroom.DefaultId}/extension-groups/demo.settings/settings", admin.AccessToken,
            new { values = new Dictionary<string, string?> { ["volume"] = "120" } }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);

        var apply = client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, $"/api/classes/{Classroom.DefaultId}/extension-groups/demo.settings/settings", admin.AccessToken,
            new { values = new Dictionary<string, string?> { ["volume"] = "30" } }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.ApplyExtensionSettings, command.Command);
        Assert.Equal("demo.settings", command.ExtensionSettings!.GroupId);
        // 部分更新：只下发本次修改的字段，未提交的 mode 保持设备原值。
        var change = Assert.Single(command.ExtensionSettings.Values);
        Assert.Equal(("volume", "30"), (change.Key, change.Value));
        Assert.Equal(UserRole.Admin, command.RequestedBy?.Role);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已保存" },
        });
        var response = await apply;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("已保存", (await response.Content.ReadFromJsonAsync<CommandResult>())!.Message);
    }

    [Fact]
    public async Task ExtensionSettings_QueuedWhileOffline_AreReplayedWhenPluginSyncsGroups()
    {
        var group = new ExtensionGroupDefinition
        {
            Id = "demo.queued",
            DisplayName = "离线补发插件",
            Settings = [new ExtensionParameter { Key = "volume", Label = "音量", Type = ExtensionParameterType.Number, Min = 0, Max = 100 }],
            Values = new Dictionary<string, string?> { ["volume"] = "50" },
        };
        // 插件曾经上报过分组，但现在离线：下发应保存为待补发并返回 202。
        _factory.Services.GetRequiredService<IStateStore>().SaveExtensionGroups(Classroom.DefaultId, [group]);
        await WaitUntilAsync(() => !_factory.Services.GetRequiredService<PeerRegistry>().HasPluginFor(Classroom.DefaultId));
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var queued = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, $"/api/classes/{Classroom.DefaultId}/extension-groups/demo.queued/settings", admin.AccessToken,
            new { values = new Dictionary<string, string?> { ["volume"] = "20" } }));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(CommandResultCodes.Queued, (await queued.Content.ReadFromJsonAsync<CommandResult>())!.Code);
        Assert.True(await PendingExistsAsync("demo.queued"));

        using var plugin = await ConnectPluginAsync();
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>()
            .PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ExtensionsSettings));
        await SendAsync(plugin, Envelope.ExtensionGroupsSync(new List<ExtensionGroupDefinition> { group }));

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.ApplyExtensionSettings, command.Command);
        Assert.Equal("20", command.ExtensionSettings!.Values["volume"]);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已保存" },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await PendingExistsAsync("demo.queued")) await Task.Delay(20, timeout.Token);
    }

    [Fact]
    public async Task CapabilitiesSync_OnlyIncludesPluginsOfClassesTheViewerCanAccess()
    {
        using var plugin = await ConnectPluginAsync();
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PeerRegistry>()
            .PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ExtensionsSettings));

        using (var adminWatch = await ConnectWatchAsync())
        {
            var sync = await ReceivePayloadAsync<CapabilitiesSync>(adminWatch, Protocol.MessageTypeCapabilitiesSync);
            var entry = Assert.Single(sync.ClassPlugins!, x => x.ClassId == Classroom.DefaultId);
            Assert.Contains(RemoteCiCapabilities.ExtensionsSettings, entry.Plugin.Capabilities);
        }

        // 只属于另一个班级的账号：默认班级的插件不能被当作它所在班级的能力来源。
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var created = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
            new CreateClassRequest { Name = "能力隔离班" }));
        created.EnsureSuccessStatusCode();
        var otherClass = (await created.Content.ReadFromJsonAsync<ClassDetail>())!;
        var user = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest
            {
                Username = "caps.isolated", DisplayName = "能力隔离", Password = "Caps-Isolated-Password-2026",
                RoleId = AccountRole.StudentId,
            }));
        user.EnsureSuccessStatusCode();
        var userId = (await user.Content.ReadFromJsonAsync<UserListItem>())!.Id;
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/classes/{otherClass.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.StudentId }] })))
            .EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            // 新账号可能被默认放入默认班级；移除后它只属于“能力隔离班”。
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ClassMemberships
                .Where(x => x.UserId == userId && x.ClassroomId == Classroom.DefaultId).ExecuteDeleteAsync();
        }

        using var isolatedWatch = await ConnectWatchAsync("caps.isolated", "Caps-Isolated-Password-2026");
        var isolated = await ReceivePayloadAsync<CapabilitiesSync>(isolatedWatch, Protocol.MessageTypeCapabilitiesSync);
        Assert.Empty(isolated.ClassPlugins!);
        Assert.Null(isolated.Plugin);
    }

    private async Task<bool> PendingExistsAsync(string groupId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PendingExtensionSettings.AnyAsync(x => x.GroupId == groupId);
    }

    [Fact]
    public async Task ExtensionSettingsCommand_FromWatchOrGenericApi_IsRejected()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.ApplyExtensionSettings,
            ExtensionSettings = new ExtensionSettingsRequest
            {
                GroupId = "demo.settings",
                Values = new Dictionary<string, string?> { ["volume"] = "1" },
            },
        });
        await SendAsync(watch, request);
        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.Equal(CommandResultCodes.Forbidden, ConvertPayload<CommandResult>(reply.Payload).Code);

        var admin = await _factory.LoginAsync();
        var generic = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/commands", admin.AccessToken, request.Payload));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, generic.StatusCode);
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
        while (store.GetLatestSnapshot(Classroom.DefaultId)?.CurrentSubject != "性能回归-2")
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

    [Fact]
    public async Task HolidayCalendar_IsSentAfterPluginDeclaresCapability()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.3.0",
            Capabilities = RemoteCiCapabilities.Current,
        }));

        var envelope = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeHolidayCalendar);
        var calendar = ConvertPayload<HolidayCalendar>(envelope.Payload);

        Assert.True(calendar.Enabled);
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
        var socket = await socketClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}{clientQuery}"),
            CancellationToken.None);
        _sockets.Add(socket);
        return socket;
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

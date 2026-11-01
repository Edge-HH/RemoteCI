using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// 服务端自动化：定时触发去重、错过窗口、执行失败通知，以及课表事件经插件执行并写入审计。
/// 定时规则用 2030 年的生效日期，避免真实时钟下运行的后台服务插手。
/// </summary>
public sealed class AutomationTests
{
    private static readonly Guid ClassId = TestWebApplicationFactory.DefaultClassId;
    private static readonly DateTimeOffset Monday10 = new(2030, 1, 7, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task ScheduleTrigger_EnqueuesOncePerDay_AndNotifiesCreatorOnFailure()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        SeedOffset(factory);
        var rule = await SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "早读提醒",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.Schedule, Weekdays = [1, 2, 3, 4, 5], Time = "10:00" },
            Target = new AutomationTarget { ClassIds = [ClassId] },
            Actions = [new AutomationAction { Kind = AutomationActionKind.ClearNotifications }],
            ValidFrom = "2030-01-07",
            ValidTo = "2030-01-07",
        });

        await WithEngineAsync(factory, engine => engine.EnqueueScheduledAsync(Monday10.AddMinutes(-1)));
        Assert.Empty(await RunsAsync(factory));

        await WithEngineAsync(factory, engine => engine.EnqueueScheduledAsync(Monday10.AddMinutes(1)));
        await WithEngineAsync(factory, engine => engine.EnqueueScheduledAsync(Monday10.AddMinutes(2)));
        var pending = Assert.Single(await RunsAsync(factory));
        Assert.Equal(AutomationRunStatus.Pending, pending.Status);

        await WithEngineAsync(factory, engine => engine.RunDueAsync(Monday10.AddMinutes(2)));
        var failed = Assert.Single(await RunsAsync(factory));
        Assert.Equal(AutomationRunStatus.Failed, failed.Status);
        Assert.Contains("插件未在线", failed.Message);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var note = await db.UserNotifications.SingleAsync(x => x.Kind == UserNotificationKinds.AutomationFailed);
        Assert.Equal("自动化「早读提醒」执行失败", note.Title);
        Assert.Equal("/Automation", note.Link);
        Assert.False((await db.AutomationRules.SingleAsync(x => x.Id == rule.Id!.Value)).LastSuccess);
    }

    [Fact]
    public async Task ScheduleTrigger_PastMissWindow_IsRecordedAsMissed()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        SeedOffset(factory);
        await SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "错过的规则",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.Schedule, Weekdays = [1], Time = "10:00" },
            Target = new AutomationTarget { ClassIds = [ClassId] },
            Actions = [new AutomationAction { Kind = AutomationActionKind.ClearNotifications }],
            ValidFrom = "2030-01-07",
            ValidTo = "2030-01-07",
        });

        await WithEngineAsync(factory, engine => engine.EnqueueScheduledAsync(Monday10.AddMinutes(30)));

        var run = Assert.Single(await RunsAsync(factory));
        Assert.Equal(AutomationRunStatus.Missed, run.Status);
    }

    [Fact]
    public async Task Validation_RejectsRulesWithoutTargetsOrActions()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "空目标",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.DeviceOnline },
            Actions = [new AutomationAction { Kind = AutomationActionKind.ClearNotifications }],
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "音量越界",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.DeviceOnline },
            Target = new AutomationTarget { ClassIds = [ClassId] },
            Actions = [new AutomationAction { Kind = AutomationActionKind.Volume, Volume = new VolumeControlRequest { Level = 150 } }],
        }));
    }

    [Fact]
    public async Task ClassEventTrigger_RunsThroughPluginAndIsAuditedAsAutomation()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        using var plugin = await ConnectPluginAsync(factory);
        await SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "放学关主界面",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.ClassEvent, Event = ClassEventKind.OnAfterSchool },
            Target = new AutomationTarget { ClassIds = [ClassId] },
            Actions = [new AutomationAction { Kind = AutomationActionKind.MainMenu, MainMenuVisible = false }],
        });

        await TestSockets.SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeEventNotify,
            Payload = new ClassEvent { Event = ClassEventKind.OnAfterSchool, Message = "放学" },
        });
        var command = await TestSockets.ReceiveAsync(plugin, Protocol.MessageTypeCommand);
        var message = JsonSerializer.Deserialize<CommandMessage>(JsonSerializer.Serialize(command.Payload), JsonDefaults.Options)!;
        Assert.Equal(CommandKind.SetMainMenuVisibility, message.Command);
        Assert.False(message.MainMenuVisible);
        await TestSockets.SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = command.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已隐藏" },
        });

        var run = await WaitForAsync(factory, db => db.AutomationRuns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Status == AutomationRunStatus.Succeeded));
        Assert.Equal(TestWebApplicationFactory.DefaultClassName, run.ClassName);
        var audit = await WaitForAsync(factory, db => db.CommandAuditEntries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MessageId == command.MessageId && x.Success != null));
        Assert.Equal(CommandAuditSource.Automation, audit.Source);
        Assert.Equal("放学关主界面", audit.SourceDetail);
        plugin.Abort();
    }

    [Fact]
    public async Task ConfigurationBackup_RoundTripsRulesAndOperationsSettings()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        var rule = await SaveRuleAsync(factory, new AutomationRuleDto
        {
            Name = "备份里的规则",
            Trigger = new AutomationTrigger { Kind = AutomationTriggerKind.DeviceOnline, DelaySeconds = 30 },
            Target = new AutomationTarget { ClassIds = [ClassId] },
            Actions = [new AutomationAction { Kind = AutomationActionKind.Volume, Volume = new VolumeControlRequest { Level = 40 } }],
        });
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<OperationsSettings>();
        await settings.SetAsync((await settings.GetAsync()) with { DeviceAlertThresholdMinutes = 25, AuditRetentionDays = 30 });
        var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
        var snapshot = await archive.CaptureAsync();
        Assert.Equal(8, snapshot.Version);

        await settings.SetAsync((await settings.GetAsync()) with { DeviceAlertThresholdMinutes = 60 });
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AutomationRules.ExecuteDeleteAsync();
        await archive.ApplyAsync(snapshot);

        using var verify = factory.Services.CreateScope();
        var restored = await verify.ServiceProvider.GetRequiredService<AutomationRuleService>().GetAsync(rule.Id!.Value);
        Assert.Equal("备份里的规则", restored.Name);
        Assert.Equal(40, Assert.Single(restored.Actions).Volume!.Level);
        var values = await verify.ServiceProvider.GetRequiredService<OperationsSettings>().GetAsync();
        Assert.Equal(25, values.DeviceAlertThresholdMinutes);
        Assert.Equal(30, values.AuditRetentionDays);
    }

    private static void SeedOffset(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<IStateStore>().SaveSnapshot(ClassId,
            new ClassStateSnapshot { ClassId = ClassId, TimeZoneOffsetMinutes = 480 });

    private static async Task<AutomationRuleDto> SaveRuleAsync(TestWebApplicationFactory factory, AutomationRuleDto rule)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<AppDbContext>().Users;
        var admin = await users.SingleAsync(x => x.UserName == TestWebApplicationFactory.AdminUsername);
        return await scope.ServiceProvider.GetRequiredService<AutomationRuleService>().SaveAsync(admin, rule);
    }

    private static async Task WithEngineAsync(TestWebApplicationFactory factory, Func<AutomationEngine, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AutomationEngine>());
    }

    private static async Task<List<AutomationRun>> RunsAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AutomationRuns.AsNoTracking().ToListAsync();
    }

    private static async Task<T> WaitForAsync<T>(TestWebApplicationFactory factory, Func<AppDbContext, Task<T?>> query) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using (var scope = factory.Services.CreateScope())
                if (await query(scope.ServiceProvider.GetRequiredService<AppDbContext>()) is { } found) return found;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task<WebSocket> ConnectPluginAsync(TestWebApplicationFactory factory)
    {
        var token = await factory.GetPluginTokenAsync();
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"), CancellationToken.None);
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!peers.HasPluginFor(ClassId)) await Task.Delay(20, timeout.Token);
        return socket;
    }

}

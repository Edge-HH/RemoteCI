using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>命令的发起入口。设备会话与 API Key 只记 Id，名称由写入线程批量查库补全。</summary>
public sealed record CommandAuditOrigin(
    CommandAuditSource Source,
    string? Detail = null,
    Guid? DeviceSessionId = null,
    Guid? ApiKeyId = null);

/// <summary>
/// 审计来源的环境上下文。后台流程（自动化、换课执行、漂移检测）用 <see cref="Begin"/> 显式声明来源；
/// REST 鉴权成功后把主体放进 HttpContext.Items，供同一请求内的命令识别 API Key 或设备会话。
/// </summary>
public static class CommandAuditContext
{
    public const string PrincipalItemKey = "RemoteCI.Audit.Principal";
    private static readonly AsyncLocal<CommandAuditOrigin?> Current = new();

    public static CommandAuditOrigin? Explicit => Current.Value;

    public static IDisposable Begin(CommandAuditSource source, string? detail = null)
    {
        var previous = Current.Value;
        Current.Value = new CommandAuditOrigin(source, detail);
        return new Scope(previous);
    }

    /// <summary>
    /// 鉴权函数是被 await 的异步方法，在其中设置 AsyncLocal 不会回流到调用方；
    /// HttpContext 是同一个对象，因此主体写入 Items 才能被同一请求后续的命令读取。
    /// </summary>
    public static AuthPrincipal? Remember(HttpContext context, AuthPrincipal? principal)
    {
        if (principal is not null) context.Items[PrincipalItemKey] = principal;
        return principal;
    }

    public static CommandAuditOrigin FromPrincipal(AuthPrincipal principal) => principal.ApiKeyId is { } keyId
        ? new CommandAuditOrigin(CommandAuditSource.ApiKey, ApiKeyId: keyId)
        : new CommandAuditOrigin(CommandAuditSource.DeviceRest, DeviceSessionId: principal.DeviceSessionId);

    /// <summary>手机或手表 WebSocket 连接直接下发的命令。</summary>
    public static CommandAuditOrigin FromSocket(AuthPrincipal principal) => new(
        principal.PeerRole == PeerRole.Mobile ? CommandAuditSource.Mobile : CommandAuditSource.Watch,
        DeviceSessionId: principal.DeviceSessionId);

    private sealed class Scope(CommandAuditOrigin? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

internal abstract record CommandAuditOperation
{
    public sealed record Insert(CommandAuditEntry Entry, Guid? DeviceSessionId, Guid? ApiKeyId) : CommandAuditOperation;
    public sealed record Complete(Guid Id, long CompletedAtUnixMs, bool Success, string Code, string Message) : CommandAuditOperation;
}

/// <summary>
/// 命令审计的入口（单例）。命令热路径只把记录放进内存队列，由 <see cref="CommandAuditWriter"/> 批量落库，
/// 不在下发和回执路径上访问数据库。队列满时丢弃并记录告警，宁可漏记也不阻塞教室控制。
/// </summary>
public sealed class CommandAuditLog(
    IHttpContextAccessor http,
    TimeProvider timeProvider,
    ILogger<CommandAuditLog> logger)
{
    public const int MaxTextLength = CommandAuditLimits.TextLength;
    private readonly Channel<CommandAuditOperation> _queue = Channel.CreateBounded<CommandAuditOperation>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal ChannelReader<CommandAuditOperation> Reader => _queue.Reader;

    /// <summary>登记一条即将下发的命令，返回审计 Id；之后必须调用 <see cref="Complete"/> 回填结果。</summary>
    public Guid Start(
        CommandMessage command,
        string messageId,
        Guid? classId,
        Guid? connectionId,
        string? deviceName,
        CommandAuditOrigin? origin = null)
    {
        origin ??= ResolveOrigin();
        var actor = command.RequestedBy;
        var entry = new CommandAuditEntry
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            CreatedAtUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ActorUserId = actor?.Id is { } actorId && actorId != Guid.Empty ? actorId : null,
            ActorName = Truncate(string.IsNullOrWhiteSpace(actor?.DisplayName) ? "系统" : actor.DisplayName, 100),
            Source = origin.Source,
            SourceDetail = origin.Detail is null ? null : Truncate(origin.Detail, 100),
            ClassId = classId,
            ConnectionId = connectionId,
            DeviceName = deviceName is null ? null : Truncate(deviceName, 80),
            Command = (int)command.Command,
            Summary = CommandAuditSummary.Describe(command),
        };
        if (!_queue.Writer.TryWrite(new CommandAuditOperation.Insert(entry, origin.DeviceSessionId, origin.ApiKeyId)))
            logger.LogWarning("审计队列已满，丢弃命令记录 {Command} ({MessageId})", command.Command, messageId);
        return entry.Id;
    }

    public void Complete(Guid auditId, CommandResult result)
    {
        var operation = new CommandAuditOperation.Complete(
            auditId,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            result.Success,
            Truncate(result.Code ?? string.Empty, 40),
            Truncate(result.Message ?? string.Empty, MaxTextLength));
        if (!_queue.Writer.TryWrite(operation))
            logger.LogWarning("审计队列已满，丢弃命令结果 {AuditId}", auditId);
    }

    private CommandAuditOrigin ResolveOrigin()
    {
        if (CommandAuditContext.Explicit is { } explicitOrigin) return explicitOrigin;
        var context = http.HttpContext;
        if (context?.Items[CommandAuditContext.PrincipalItemKey] is AuthPrincipal principal)
            return CommandAuditContext.FromPrincipal(principal);
        if (context?.User.Identity?.IsAuthenticated == true)
            return new CommandAuditOrigin(
                CommandAuditSource.WebUi,
                context.Request.Path.StartsWithSegments("/Mobile") ? "手机网页" : null);
        return new CommandAuditOrigin(CommandAuditSource.System);
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>把队列中的审计记录批量写入数据库，并按保留天数定期清理。</summary>
public sealed class CommandAuditWriter(
    CommandAuditLog log,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<CommandAuditWriter> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(12);
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = log.Reader;
        var batch = new List<CommandAuditOperation>(BatchSize);
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && reader.TryRead(out var operation)) batch.Add(operation);
                try
                {
                    await FlushAsync(batch, stoppingToken);
                    await PruneIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 审计是旁路数据：写库失败只记日志，不能拖垮服务端。
                    logger.LogWarning(ex, "写入命令审计记录失败，丢弃 {Count} 条", batch.Count);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }

    private async Task FlushAsync(IReadOnlyList<CommandAuditOperation> batch, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inserts = batch.OfType<CommandAuditOperation.Insert>().ToList();
        if (inserts.Count > 0)
        {
            var classIds = inserts.Select(x => x.Entry.ClassId).OfType<Guid>().Distinct().ToList();
            var keyIds = inserts.Select(x => x.ApiKeyId).OfType<Guid>().Distinct().ToList();
            var sessionIds = inserts.Select(x => x.DeviceSessionId).OfType<Guid>().Distinct().ToList();
            var classNames = await db.Classrooms.AsNoTracking().Where(x => classIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var keyNames = await db.UserApiKeys.AsNoTracking().Where(x => keyIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var sessionNames = await db.DeviceSessions.AsNoTracking().Where(x => sessionIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.DeviceName, ct);
            foreach (var insert in inserts)
            {
                var entry = insert.Entry;
                if (entry.ClassId is { } classId && classNames.TryGetValue(classId, out var className))
                    entry.ClassName = className;
                entry.SourceDetail ??= insert.ApiKeyId is { } keyId && keyNames.TryGetValue(keyId, out var keyName)
                    ? keyName
                    : insert.DeviceSessionId is { } sessionId && sessionNames.TryGetValue(sessionId, out var deviceName)
                        ? deviceName
                        : null;
                db.CommandAuditEntries.Add(entry);
            }
        }

        var pending = inserts.ToDictionary(x => x.Entry.Id, x => x.Entry);
        var completes = batch.OfType<CommandAuditOperation.Complete>().ToList();
        var stored = completes.Where(x => !pending.ContainsKey(x.Id)).Select(x => x.Id).ToList();
        if (stored.Count > 0)
            foreach (var entry in await db.CommandAuditEntries.Where(x => stored.Contains(x.Id)).ToListAsync(ct))
                pending[entry.Id] = entry;
        foreach (var complete in completes)
        {
            // 早于插入就已被清理或队列丢弃的记录无处回填，忽略即可。
            if (!pending.TryGetValue(complete.Id, out var entry)) continue;
            entry.CompletedAtUnixMs = complete.CompletedAtUnixMs;
            entry.Success = complete.Success;
            entry.ResultCode = complete.Code;
            entry.ResultMessage = complete.Message;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PruneIfDueAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextPrune) return;
        _nextPrune = now + PruneInterval;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var days = await db.SystemMetadata.AsNoTracking().Select(x => x.AuditRetentionDays).FirstOrDefaultAsync(ct);
        var cutoff = now.AddDays(-CommandAuditRetention.Clamp(days)).ToUnixTimeMilliseconds();
        var removed = await db.CommandAuditEntries.Where(x => x.CreatedAtUnixMs < cutoff).ExecuteDeleteAsync(ct);
        if (removed > 0) logger.LogInformation("已清理 {Count} 条过期命令审计记录", removed);
    }
}

public static class CommandAuditRetention
{
    public const int Default = CommandAuditLimits.DefaultRetentionDays;
    public const int Min = 7;
    public const int Max = 3650;

    /// <summary>旧数据库升级后该列为 0，按默认值处理。</summary>
    public static int Clamp(int days) => days <= 0 ? Default : Math.Clamp(days, Min, Max);
}

/// <summary>把命令载荷转成可读摘要：只取标题、动作和参数名等必要信息，不保存音频、文件内容或档案 JSON。</summary>
public static class CommandAuditSummary
{
    public static string Label(CommandKind kind) => kind switch
    {
        CommandKind.ChangeSchedule => "换课",
        CommandKind.SendNotification => "发送通知",
        CommandKind.ClearNotifications => "清除提醒",
        CommandKind.SetMainMenuVisibility => "主界面显隐",
        CommandKind.Power => "电源",
        CommandKind.Volume => "音量",
        CommandKind.RunExtension => "运行扩展",
        CommandKind.TeacherComing => "老师来了",
        CommandKind.SendVoiceMessage => "语音消息",
        CommandKind.UpgradePlugins => "升级插件",
        CommandKind.UpgradeClassIsland => "升级 ClassIsland",
        CommandKind.RefreshSoftwareInventory => "刷新版本清单",
        CommandKind.InstallPlugins => "安装插件",
        CommandKind.UninstallPlugins => "卸载插件",
        CommandKind.SetPluginEnabled => "启用或禁用插件",
        CommandKind.SetPluginManagementPolicy => "插件管理策略",
        CommandKind.SetExtensionAccess => "扩展接入授权",
        CommandKind.DistributeProfile => "分发档案",
        CommandKind.UpdateTimeLayout => "修改时间表",
        CommandKind.JoinManagement => "加入集控",
        CommandKind.RestartClassIsland => "重启 ClassIsland",
        CommandKind.SetSubjectTeacher => "设置科目教师",
        CommandKind.ExecuteTerminalCommand => "远程终端",
        CommandKind.SendFile => "分发文件",
        CommandKind.ApplyExtensionSettings => "扩展设置",
        CommandKind.ApplyProfile => "应用档案",
        CommandKind.ReadProfile => "收集档案",
        CommandKind.CaptureScreen => "屏幕截图",
        CommandKind.ApplyMainWindowLayout => "上传主界面文件",
        CommandKind.ReadMainWindowLayout => "获取主界面文件",
        CommandKind.InstallPluginPackages => "安装插件包",
        CommandKind.StopAppGuard => "关闭守护",
        CommandKind.UninstallAppGuard => "卸载守护",
        _ => $"命令 {(int)kind}",
    };

    public static string Describe(CommandMessage command)
    {
        var detail = command.Command switch
        {
            CommandKind.ChangeSchedule when command.ScheduleChange is { } change => DescribeChange(change),
            CommandKind.SendNotification when command.Notification is { } note =>
                $"「{Clip(note.Title, 40)}」{Clip(note.Message, 120)}",
            CommandKind.SetMainMenuVisibility => command.MainMenuVisible == true ? "显示" : "隐藏",
            CommandKind.ApplyMainWindowLayout when command.MainWindowLayout is { } layout =>
                layout.Mode == MainWindowLayoutApplyMode.CreateNew
                    ? $"新建「{Clip(layout.Name, 40)}」{(layout.Activate ? "并使用" : "，不切换")}"
                    : "覆盖当前主界面文件",
            CommandKind.Power => command.PowerAction switch
            {
                PowerActionKind.Shutdown => "关机",
                PowerActionKind.Restart => "重启",
                PowerActionKind.Sleep => "睡眠",
                PowerActionKind.Hibernate => "休眠",
                _ => "未知动作",
            },
            CommandKind.Volume when command.Volume is { } volume => string.Join("，", new[]
            {
                volume.Level is { } level ? $"音量 {level}" : null,
                volume.Muted is { } muted ? (muted ? "静音" : "取消静音") : null,
            }.OfType<string>()),
            CommandKind.RunExtension => $"{command.ExtensionId}{DescribeArgs(command.ExtensionArgs)}",
            CommandKind.SendVoiceMessage when command.VoiceMessage is { } voice =>
                $"约 {EstimateBytes(voice.AudioBase64) / (VoiceMessageRequest.SampleRate * 2.0):0.#} 秒" +
                (voice.ForceComplete ? "，强制播放完成" : string.Empty),
            CommandKind.UpgradePlugins or CommandKind.UpgradeClassIsland when command.SoftwareUpgrade is { } upgrade =>
                string.Join("，", new[]
                {
                    upgrade.PluginIds is { Count: > 0 } ids ? string.Join("、", ids.Take(10)) : null,
                    upgrade.Force ? "强制" : null,
                }.OfType<string>()),
            CommandKind.InstallPlugins or CommandKind.UninstallPlugins or CommandKind.SetPluginEnabled
                when command.PluginManagement is { } plugins =>
                $"{plugins.Action}：{string.Join("、", plugins.PluginIds.Take(10))}" +
                (plugins.GrantExtensionAccess ? "，同时授权接入 RemoteCI" : string.Empty),
            CommandKind.InstallPluginPackages when command.PluginPackages is { } packages =>
                $"{(packages.Mode == PluginPackageInstallMode.Upgrade ? "更新" : "安装")}：" +
                string.Join("、", packages.Packages.Take(10).Select(x => $"{x.PluginId} {x.Version}")) +
                (packages.Force ? "，强制" : string.Empty) +
                (packages.GrantExtensionAccess ? "，同时授权接入 RemoteCI" : string.Empty),
            CommandKind.SetExtensionAccess when command.ExtensionAccess is { } access =>
                $"{(access.Action == ExtensionAccessAction.Lock ? "保持授权" : "解除保持")}：" +
                string.Join("、", access.PluginIds.Take(10)),
            CommandKind.DistributeProfile when command.ProfileDistribution is { } distribution =>
                $"{DescribeSections(distribution.Sections)}{(distribution.ReplaceCurrentProfile ? "，写入当前档案" : string.Empty)}",
            CommandKind.UpdateTimeLayout when command.TimeLayoutUpdate is { } layout =>
                $"{Clip(layout.Name, 40)}，{layout.Points.Count} 个时间点",
            CommandKind.SetSubjectTeacher when command.SubjectTeacher is { } teacher =>
                string.IsNullOrWhiteSpace(teacher.TeacherName) ? "清除教师" : $"教师 {Clip(teacher.TeacherName, 40)}",
            CommandKind.ExecuteTerminalCommand when command.TerminalCommand is { } terminal =>
                Clip(terminal.Command.Trim(), 200) +
                (string.IsNullOrWhiteSpace(terminal.WorkingDirectory) ? string.Empty : $"（目录 {Clip(terminal.WorkingDirectory, 80)}）"),
            CommandKind.SendFile when command.FileDistribution is { } file =>
                $"{Clip(file.FileName, 80)}，{FormatSize(file.IsTransfer ? file.Size : EstimateBytes(file.ContentBase64))}{(file.IsTransfer ? "（中转）" : string.Empty)}，" +
                $"{file.TargetFolder switch { FileTargetFolder.Downloads => "下载", FileTargetFolder.Documents => "文档", _ => "桌面" }}" +
                (file.Overwrite ? "，覆盖同名" : string.Empty),
            CommandKind.ApplyExtensionSettings when command.ExtensionSettings is { } settings =>
                $"{settings.GroupId}：{string.Join("、", settings.Values.Keys.Take(10))}",
            CommandKind.ApplyProfile when command.ProfileApply is { } apply =>
                $"{apply.Mode switch
                {
                    ProfileApplyMode.MergeCurrent => "更新当前档案",
                    ProfileApplyMode.ReplaceSections => "整体替换所选类别",
                    ProfileApplyMode.CreateAndActivate => "创建并启用新档案",
                    ProfileApplyMode.TempLayers => "作为临时层下发",
                    _ => "应用档案",
                }}{(apply.Mode == ProfileApplyMode.TempLayers ? string.Empty : "，" + DescribeSections(apply.Sections))}" +
                (string.IsNullOrWhiteSpace(apply.ImportProfileName) ? string.Empty : $"，{Clip(apply.ImportProfileName, 40)}"),
            _ => null,
        };
        var text = string.IsNullOrWhiteSpace(detail) ? Label(command.Command) : $"{Label(command.Command)}：{detail}";
        return CommandAuditLog.Truncate(text, CommandAuditLog.MaxTextLength);
    }

    private static string DescribeChange(ScheduleChangeRequest change)
    {
        var action = change.Mode == ScheduleChangeMode.Exchange && change.TargetIndex is { } target
            ? $"第{change.SourceIndex + 1}节与第{target + 1}节互换"
            : $"第{change.SourceIndex + 1}节替换科目";
        return $"{change.Date} {action}{(change.Permanent ? "，永久" : string.Empty)}";
    }

    private static string DescribeArgs(IReadOnlyDictionary<string, string?>? args)
    {
        if (args is null || args.Count == 0) return string.Empty;
        var builder = new StringBuilder("（");
        builder.Append(string.Join("，", args.Take(8).Select(x => $"{Clip(x.Key, 30)}={Clip(x.Value ?? string.Empty, 40)}")));
        if (args.Count > 8) builder.Append("…");
        return builder.Append('）').ToString();
    }

    private static string DescribeSections(ProfileDistributionSection sections)
    {
        var names = new List<string>();
        if (sections.HasFlag(ProfileDistributionSection.TimeLayouts)) names.Add("时间表");
        if (sections.HasFlag(ProfileDistributionSection.ClassPlans)) names.Add("课表");
        if (sections.HasFlag(ProfileDistributionSection.Subjects)) names.Add("科目");
        return names.Count == 0 ? "未选择类别" : string.Join("、", names);
    }

    private static long EstimateBytes(string? base64) => string.IsNullOrEmpty(base64) ? 0 : base64.Length / 4L * 3;

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static string Clip(string? value, int max) => CommandAuditLog.Truncate(value ?? string.Empty, max);
}

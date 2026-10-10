using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 把内存状态缓存中的课表与扩展声明写入数据库，并在启动时载回：
/// 插件推送后标记班级为待写入，后台按班级合并写入最新值；停止时写完剩余班级。
/// </summary>
public sealed class ClassStateCacheService(
    StateStore inner,
    IServiceScopeFactory scopes,
    ILogger<ClassStateCacheService> logger) : BackgroundService
{
    private readonly Channel<Guid> _dirty = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    /// <summary>班级的课表或扩展声明发生变化；同一班级排队期间的多次变化只写入一次最新值。</summary>
    public void MarkDirty(Guid classId)
    {
        if (_queued.TryAdd(classId, 0)) _dirty.Writer.TryWrite(classId);
    }

    /// <summary>启动时把数据库中的副本载入内存；须在接受插件与客户端连接之前调用。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ClassStateCaches.AsNoTracking().ToListAsync(ct);
        foreach (var row in rows)
        {
            if (Read<ScheduleBundle>(row.ScheduleJson, row.ClassroomId) is { } schedule)
            {
                schedule.ClassId = row.ClassroomId;
                inner.SaveSchedule(row.ClassroomId, schedule);
            }
            if (Read<List<ExtensionDefinition>>(row.ExtensionsJson, row.ClassroomId) is { } extensions)
                inner.SaveExtensions(row.ClassroomId, extensions);
            if (Read<List<ExtensionGroupDefinition>>(row.ExtensionGroupsJson, row.ClassroomId) is { } groups)
                inner.SaveExtensionGroups(row.ClassroomId, groups);
        }
        if (rows.Count > 0) logger.LogInformation("已载入 {Count} 个班级缓存的课表与扩展声明", rows.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var classId in _dirty.Reader.ReadAllAsync(stoppingToken))
                await PersistAsync(classId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止时由 StopAsync 写完剩余班级。
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        while (_dirty.Reader.TryRead(out var classId))
            await PersistAsync(classId, cancellationToken);
    }

    private async Task PersistAsync(Guid classId, CancellationToken ct)
    {
        // 先出队再读取：写入期间到来的新变化会重新排队，不会丢失。
        _queued.TryRemove(classId, out _);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Classrooms.AnyAsync(x => x.Id == classId, ct)) return;
            var row = await db.ClassStateCaches.SingleOrDefaultAsync(x => x.ClassroomId == classId, ct);
            if (row is null) db.ClassStateCaches.Add(row = new ClassStateCache { ClassroomId = classId });
            row.ScheduleJson = Write(inner.GetSourceSchedule(classId));
            row.ExtensionsJson = Write(inner.GetLatestExtensions(classId));
            row.ExtensionGroupsJson = Write(inner.GetLatestExtensionGroups(classId));
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ObjectDisposedException)
        {
            // 班级刚被删除或数据库正在关闭：缓存只是加速恢复的副本，写入失败不影响实时数据。
            logger.LogWarning(ex, "写入班级 {ClassId} 的课表与扩展缓存失败", classId);
        }
    }

    private static string? Write<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);

    private T? Read<T>(string? json, Guid classId) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "忽略班级 {ClassId} 无法解析的缓存", classId);
            return null;
        }
    }
}

/// <summary>写入课表与扩展声明时同步标记持久化的状态缓存；读取全部来自内存。</summary>
public sealed class PersistentStateStore(StateStore inner, ClassStateCacheService cache) : IStateStore
{
    public void SaveSnapshot(Guid classId, ClassStateSnapshot snapshot) => inner.SaveSnapshot(classId, snapshot);
    public ClassStateSnapshot? GetLatestSnapshot(Guid classId) => inner.GetLatestSnapshot(classId);

    public void SaveSchedule(Guid classId, ScheduleBundle schedule)
    {
        inner.SaveSchedule(classId, schedule);
        cache.MarkDirty(classId);
    }

    public ScheduleBundle? GetLatestSchedule(Guid classId) => inner.GetLatestSchedule(classId);
    public ScheduleBundle? GetSourceSchedule(Guid classId) => inner.GetSourceSchedule(classId);
    public void SaveEvent(Guid classId, ClassEvent @event) => inner.SaveEvent(classId, @event);
    public ClassEvent? GetLatestEvent(Guid classId) => inner.GetLatestEvent(classId);

    public void SaveExtensions(Guid classId, IReadOnlyList<ExtensionDefinition> extensions)
    {
        inner.SaveExtensions(classId, extensions);
        cache.MarkDirty(classId);
    }

    public IReadOnlyList<ExtensionDefinition>? GetLatestExtensions(Guid classId) => inner.GetLatestExtensions(classId);

    public void SaveExtensionGroups(Guid classId, IReadOnlyList<ExtensionGroupDefinition> groups)
    {
        inner.SaveExtensionGroups(classId, groups);
        cache.MarkDirty(classId);
    }

    public IReadOnlyList<ExtensionGroupDefinition>? GetLatestExtensionGroups(Guid classId) =>
        inner.GetLatestExtensionGroups(classId);
}

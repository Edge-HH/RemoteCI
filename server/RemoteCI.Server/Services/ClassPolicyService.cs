using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 班级策略：按班级保存（Classroom.PolicyJson）并在插件连接、策略变更时下发。
/// 策略是期望状态，可重复下发；插件本地也缓存一份，离线启动时继续生效。
/// </summary>
public sealed class ClassPolicyService(
    IServiceScopeFactory scopes,
    PeerRegistry peers,
    ILogger<ClassPolicyService> logger)
{
    /// <summary>读取某班策略；未设置或内容损坏时返回默认策略（不限制）。</summary>
    public async Task<ClassPolicy> GetAsync(Guid classId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var json = await db.Classrooms.AsNoTracking()
            .Where(x => x.Id == classId)
            .Select(x => x.PolicyJson)
            .SingleOrDefaultAsync(ct);
        return ClassPolicy.FromJson(json);
    }

    /// <summary>保存一个或多个班级的策略，并立即下发给每个班级的在线插件。</summary>
    public async Task SaveAsync(IReadOnlyCollection<Guid> classIds, ClassPolicy policy, CancellationToken ct = default)
    {
        var ids = classIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;
        var json = JsonSerializer.Serialize(policy, JsonDefaults.Options);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var classrooms = await db.Classrooms.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var classroom in classrooms)
            {
                classroom.PolicyJson = json;
                classroom.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
        }

        // 同一班级可能有多台设备，策略必须覆盖全部在线插件；命令回执只代表其中一台。
        foreach (var classId in ids)
            await SendToClassAsync(classId, policy, ct);
    }

    /// <summary>把策略推送到指定班级的全部在线插件（未声明 class.policy 的旧插件跳过）。</summary>
    public async Task<int> SendToClassAsync(Guid classId, ClassPolicy policy, CancellationToken ct = default) =>
        await peers.BroadcastToClassPluginsAsync(classId, RemoteCiCapabilities.ClassPolicy, Envelope.ClassPolicy(policy), ct);

    /// <summary>插件上报能力后补发当前班级策略；旧插件不会收到。</summary>
    public async Task SendToConnectionAsync(Guid connectionId, Guid classId, CancellationToken ct = default)
    {
        var policy = await GetAsync(classId, ct);
        await peers.SendToPluginConnectionAsync(connectionId, Envelope.ClassPolicy(policy), ct);
        logger.LogDebug("已向连接 {ConnectionId} 下发班级 {ClassId} 的策略", connectionId, classId);
    }
}

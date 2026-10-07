using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>全局访客入口设置；班级级“是否开放访客”开关在 Classroom.VisitorAccessEnabled 上。</summary>
public sealed class VisitorAccessSettings(AppDbContext db)
{
    public async Task<bool> GetAutoEnterAsync(CancellationToken ct = default)
    {
        return await db.SystemMetadata.AsNoTracking()
            .Where(metadata => metadata.Id == 1)
            .Select(metadata => metadata.AutoEnterVisitorPage)
            .SingleAsync(ct);
    }

    public async Task<bool> SetAutoEnterAsync(bool autoEnter, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        // 只保存管理员意图；是否真正生效由读取方结合“存在开放访客的班级”判断，
        // 避免开关班级后全局意图被静默改写。
        metadata.AutoEnterVisitorPage = autoEnter;
        await db.SaveChangesAsync(ct);
        return metadata.AutoEnterVisitorPage;
    }

    /// <summary>是否存在开放访客的班级；决定访客页与自动进入是否可用。</summary>
    public async Task<bool> AnyVisitorClassEnabledAsync(CancellationToken ct = default) =>
        await db.Classrooms.AsNoTracking().AnyAsync(x => x.VisitorAccessEnabled, ct);

    /// <summary>开放访客的班级，供访客页渲染班级选择；访客数量级很小，直接取回。</summary>
    public async Task<IReadOnlyList<Classroom>> ListVisitorClassroomsAsync(CancellationToken ct = default)
    {
        var classrooms = await db.Classrooms.AsNoTracking()
            .Where(x => x.VisitorAccessEnabled)
            .Include(x => x.GroupAssignments)
            .ToListAsync(ct);
        return classrooms.OrderBy(x => x.CreatedAt).ToList();
    }
}

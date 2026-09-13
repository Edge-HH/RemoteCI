using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed record VisitorAccessState(bool Enabled, bool AutoEnter);

public sealed class VisitorAccessSettings(AppDbContext db)
{
    public async Task<VisitorAccessState> GetAsync(CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking()
            .Where(metadata => metadata.Id == 1)
            .Select(metadata => new { metadata.VisitorAccessEnabled, metadata.AutoEnterVisitorPage })
            .SingleAsync(ct);
        return Normalize(row.VisitorAccessEnabled, row.AutoEnterVisitorPage);
    }

    public async Task<VisitorAccessState> SetAsync(bool enabled, bool autoEnter, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        var state = Normalize(enabled, autoEnter);
        metadata.VisitorAccessEnabled = state.Enabled;
        metadata.AutoEnterVisitorPage = state.AutoEnter;
        await db.SaveChangesAsync(ct);
        return state;
    }

    private static VisitorAccessState Normalize(bool enabled, bool autoEnter) =>
        new(enabled, enabled && autoEnter);
}

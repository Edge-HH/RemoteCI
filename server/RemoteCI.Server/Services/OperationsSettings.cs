using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>系统设置“常规”tab 的“集控运维”区的全局设置：操作记录保留天数、设备离线告警与每日档案差异检查。</summary>
public sealed record OperationsSettingsValues(
    int AuditRetentionDays,
    bool DeviceAlertsEnabled,
    int DeviceAlertThresholdMinutes,
    bool DeviceAlertNotifyAdmins,
    bool DeviceAlertNotifyClassHeads,
    bool DeviceAlertSchoolHoursOnly,
    bool ProfileDriftCheckEnabled = true,
    string ProfileDriftCheckTime = "12:30");

public sealed class OperationsSettings(AppDbContext db)
{
    public async Task<OperationsSettingsValues> GetAsync(CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SystemMetadata();
        return new OperationsSettingsValues(
            CommandAuditRetention.Clamp(row.AuditRetentionDays),
            row.DeviceAlertsEnabled,
            Math.Clamp(row.DeviceAlertThresholdMinutes, DeviceAlertDefaults.MinThresholdMinutes, DeviceAlertDefaults.MaxThresholdMinutes),
            row.DeviceAlertNotifyAdmins,
            row.DeviceAlertNotifyClassHeads,
            row.DeviceAlertSchoolHoursOnly,
            row.ProfileDriftCheckEnabled,
            NormalizeTime(row.ProfileDriftCheckTime) ?? "12:30");
    }

    /// <summary>每日检查时刻统一为 HH:mm；无效时返回 null。</summary>
    public static string? NormalizeTime(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var time)
            ? time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public async Task SetAsync(OperationsSettingsValues values, CancellationToken ct = default)
    {
        if (values.AuditRetentionDays is < CommandAuditRetention.Min or > CommandAuditRetention.Max)
            throw new ArgumentException($"操作记录保留天数须在 {CommandAuditRetention.Min}-{CommandAuditRetention.Max} 之间。");
        if (values.DeviceAlertThresholdMinutes is < DeviceAlertDefaults.MinThresholdMinutes or > DeviceAlertDefaults.MaxThresholdMinutes)
            throw new ArgumentException(
                $"离线告警阈值须在 {DeviceAlertDefaults.MinThresholdMinutes}-{DeviceAlertDefaults.MaxThresholdMinutes} 分钟之间。");
        var driftTime = NormalizeTime(values.ProfileDriftCheckTime) ?? throw new ArgumentException("档案差异检查时间格式应为 HH:mm。");
        var row = await db.SystemMetadata.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            row = new SystemMetadata();
            db.SystemMetadata.Add(row);
        }
        row.AuditRetentionDays = values.AuditRetentionDays;
        row.DeviceAlertsEnabled = values.DeviceAlertsEnabled;
        row.DeviceAlertThresholdMinutes = values.DeviceAlertThresholdMinutes;
        row.DeviceAlertNotifyAdmins = values.DeviceAlertNotifyAdmins;
        row.DeviceAlertNotifyClassHeads = values.DeviceAlertNotifyClassHeads;
        row.DeviceAlertSchoolHoursOnly = values.DeviceAlertSchoolHoursOnly;
        row.ProfileDriftCheckEnabled = values.ProfileDriftCheckEnabled;
        if (row.ProfileDriftCheckTime != driftTime)
        {
            row.ProfileDriftCheckTime = driftTime;
            // 改了时刻后当天按新时刻重新判断是否还要检查。
            row.ProfileDriftLastCheckDate = null;
        }
        await db.SaveChangesAsync(ct);
    }
}

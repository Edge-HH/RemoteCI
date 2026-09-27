using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 批量控制页的设备目录与定向命令投递。设备目录同时读取在线连接和离线凭据快照，
/// 让页面在设备离线时仍能解释“为什么不能选择”。
/// </summary>
public sealed class DeviceInventoryService(
    AppDbContext db,
    PeerRegistry peers,
    ClassroomService classrooms)
{
    public async Task<IReadOnlyList<DeviceInventory>> ListAsync(CancellationToken ct = default)
    {
        var credentials = await db.PluginCredentials
            .Include(x => x.Classroom)
            .AsNoTracking()
            .Where(x => x.Assigned)
            .OrderBy(x => x.ClassroomId)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);
        var online = peers.GetPluginDeviceSnapshots()
            .Where(x => x.PluginCredentialId is not null)
            .GroupBy(x => x.PluginCredentialId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.SoftwareInventoryAt ?? DateTimeOffset.MinValue).First());

        return credentials.Select(credential =>
        {
            online.TryGetValue(credential.Id, out var snapshot);
            var inventory = snapshot?.SoftwareInventory ?? TryParseInventory(credential.SoftwareInventoryJson);
            var capabilities = snapshot?.EffectiveCapabilities ?? [];
            return new DeviceInventory(
                snapshot?.ConnectionId,
                credential.Id,
                string.IsNullOrWhiteSpace(inventory?.DeviceName) ? credential.Name : inventory.DeviceName,
                credential.ClassroomId,
                credential.Assigned ? credential.Classroom?.Name ?? "默认班级" : "未分配",
                snapshot is not null,
                FindVersion(inventory?.Plugins, "remoteci.plugin") ?? snapshot?.SoftwareVersion ?? "未知",
                FindVersion(inventory?.Applications, "classisland") ?? "未知",
                inventory?.GeneratedAt ?? credential.SoftwareInventoryAt,
                capabilities.Contains(RemoteCiCapabilities.SoftwareUpgradePlugins, StringComparer.Ordinal),
                capabilities.Contains(RemoteCiCapabilities.SoftwareUpgradeClassIsland, StringComparer.Ordinal),
                capabilities,
                inventory?.Applications ?? [],
                inventory?.Plugins ?? [],
                inventory?.LastUpdate,
                credential.Assigned,
                credential.ClassNameRemark);
        }).ToList();
    }

    /// <summary>
    /// 把“班级/分组选择”和“具体设备选择”合并为最终投递列表。
    /// 班级级选择只取该班第一台在线设备，避免同班多设备重复执行；显式设备选择保留每一台。
    /// </summary>
    public async Task<DeviceDispatchPlan> ResolveAsync(
        IReadOnlyCollection<Guid> classIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken ct = default)
    {
        var devices = await ListAsync(ct);
        var targets = new List<DeviceInventory>();
        var failures = new List<DeviceCommandResult>();
        var explicitIds = connectionIds.ToHashSet();

        foreach (var device in devices.Where(x => x.ConnectionId is { } id && explicitIds.Contains(id)))
            targets.Add(device);

        if (classIds.Count > 0 || groupIds.Count > 0)
        {
            var targetClassIds = await classrooms.ResolveTargetClassIdsAsync(classIds.ToList(), groupIds.ToList(), ct);
            var classNames = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
            foreach (var classId in targetClassIds)
            {
                var primary = devices
                    .Where(x => x.Assigned && x.ClassId == classId && x.Online && x.ConnectionId is not null)
                    .OrderBy(x => x.InventoryAt ?? DateTimeOffset.MinValue)
                    .ThenBy(x => x.DeviceName, StringComparer.CurrentCulture)
                    .FirstOrDefault();
                if (primary is null)
                {
                    failures.Add(new DeviceCommandResult(
                        Guid.Empty,
                        null,
                        classId,
                        classNames.GetValueOrDefault(classId, classId.ToString()),
                        false,
                        "该班级的插件未在线。"));
                    continue;
                }
                targets.Add(primary);
            }
        }

        return new DeviceDispatchPlan(
            targets
                .GroupBy(x => x.ConnectionId)
                .Where(group => group.Key is not null)
                .Select(group => group.First())
                .ToList(),
            failures);
    }

    public async Task<IReadOnlyList<DeviceCommandResult>> DispatchAsync(
        IReadOnlyCollection<DeviceInventory> targets,
        Func<DeviceInventory, CommandMessage> commandFactory,
        string actionName,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var results = new List<DeviceCommandResult>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target.ConnectionId is not { } connectionId)
            {
                results.Add(new DeviceCommandResult(
                    target.CredentialId, null, target.ClassId, target.DeviceName, false, "设备已离线。"));
                continue;
            }

            var command = commandFactory(target);
            command.ClassId ??= target.ClassId;
            var result = await peers.SendCommandAndWaitToConnectionAsync(
                command,
                connectionId,
                timeout,
                ct);
            results.Add(new DeviceCommandResult(
                target.CredentialId,
                connectionId,
                target.ClassId,
                target.DeviceName,
                result.Success,
                result.Success ? $"{actionName}已下发。" : result.Message));
        }
        return results;
    }

    private static SoftwareInventory? TryParseInventory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SoftwareInventory>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindVersion(IEnumerable<SoftwarePackageInfo>? packages, string id) =>
        packages?.FirstOrDefault(package => string.Equals(package.Id, id, StringComparison.Ordinal))?.Version;
}

public sealed record DeviceInventory(
    Guid? ConnectionId,
    Guid CredentialId,
    string DeviceName,
    Guid ClassId,
    string ClassName,
    bool Online,
    string RemoteCiVersion,
    string ClassIslandVersion,
    DateTimeOffset? InventoryAt,
    bool SupportsPluginUpgrade,
    bool SupportsClassIslandUpgrade,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<SoftwarePackageInfo> Applications,
    IReadOnlyList<SoftwarePackageInfo> Plugins,
    SoftwareUpdateStatus? LastUpdate,
    bool Assigned,
    string? ClassNameRemark);

public sealed record DeviceCommandResult(
    Guid CredentialId,
    Guid? ConnectionId,
    Guid ClassId,
    string DeviceName,
    bool Success,
    string Message);

public sealed record DeviceDispatchPlan(
    IReadOnlyList<DeviceInventory> Targets,
    IReadOnlyList<DeviceCommandResult> Failures);




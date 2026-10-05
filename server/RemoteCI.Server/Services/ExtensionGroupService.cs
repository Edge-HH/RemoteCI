using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 扩展分组的服务端视图：把插件上报的分组、设置字段与扩展功能按“插件”归类，
/// 供控制页、批量控制页与扩展设置页共用，并负责扩展设置的权限复核、校验与下发。
/// 分组与当前设置值只缓存在内存中，以设备上报为准；插件重连后自动恢复。
/// </summary>
public sealed class ExtensionGroupService(
    IStateStore store,
    PeerRegistry peers,
    ClassroomService classrooms,
    ClassAccessService access,
    PendingExtensionSettingsService pending)
{
    /// <summary>未声明分组或分组未注册的扩展统一归入的占位分组 Id（不会与合法扩展分组 Id 冲突）。</summary>
    public const string UngroupedId = "";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    /// <summary>当前班级的分组视图：分组顺序按插件上报顺序，未归组扩展放在最后的“其他扩展”。</summary>
    public IReadOnlyList<ExtensionGroupView> BuildForClass(Guid classId, IReadOnlyList<ExtensionDefinition> extensions) =>
        Build(store.GetLatestExtensionGroups(classId) ?? [], extensions);

    /// <summary>使用该班缓存的全部扩展构建分组视图；用于设置页与 API，不按账号过滤扩展。</summary>
    public IReadOnlyList<ExtensionGroupView> BuildForClass(Guid classId) =>
        BuildForClass(classId, store.GetLatestExtensions(classId) ?? []);

    /// <summary>
    /// 全部班级的分组并集：同一分组 Id 取第一个声明了设置字段的班级定义，
    /// 扩展按 Id 去重；用于批量控制与扩展设置页的“统一下发”。
    /// </summary>
    public async Task<IReadOnlyList<ExtensionGroupView>> BuildForAllClassesAsync(
        Func<ExtensionDefinition, bool>? includeExtension = null, CancellationToken ct = default)
    {
        var groups = new List<ExtensionGroupDefinition>();
        var extensions = new List<ExtensionDefinition>();
        foreach (var classroom in await classrooms.ListAsync(ct))
        {
            foreach (var group in store.GetLatestExtensionGroups(classroom.Id) ?? [])
            {
                var index = groups.FindIndex(x => string.Equals(x.Id, group.Id, StringComparison.Ordinal));
                if (index < 0) groups.Add(group);
                else if (!groups[index].HasSettings && group.HasSettings) groups[index] = group;
            }
            foreach (var extension in store.GetLatestExtensions(classroom.Id) ?? [])
            {
                if (extensions.All(x => !string.Equals(x.Id, extension.Id, StringComparison.Ordinal)) &&
                    (includeExtension?.Invoke(extension) ?? true))
                    extensions.Add(extension);
            }
        }
        return Build(groups, extensions);
    }

    /// <summary>上报了指定分组或有待补发设置的班级及其当前值，用于设置页对比各班差异。</summary>
    public async Task<IReadOnlyList<ExtensionGroupClassState>> ListClassStatesAsync(
        string groupId, CancellationToken ct = default)
    {
        var pendingByClass = await pending.ListByGroupAsync(groupId, ct);
        var states = new List<ExtensionGroupClassState>();
        foreach (var classroom in await classrooms.ListAsync(ct))
        {
            var group = FindGroup(classroom.Id, groupId);
            var queued = pendingByClass.GetValueOrDefault(classroom.Id);
            if (group is null && queued is null) continue;
            states.Add(new ExtensionGroupClassState(
                classroom.Id,
                classroom.Name,
                peers.HasPluginFor(classroom.Id),
                group?.Values ?? new Dictionary<string, string?>(),
                queued));
        }
        return states;
    }

    /// <summary>插件离线时保存为待补发，返回 QUEUED 回执。</summary>
    public async Task<CommandResult> QueueAsync(
        UserProfile user, Guid classId, string groupId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        await pending.RecordAsync(classId, groupId, values, user.Id, ct);
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Queued,
            Message = "插件离线，设置已保存，插件上线后自动补发",
        };
    }

    public ExtensionGroupDefinition? FindGroup(Guid classId, string groupId) =>
        store.GetLatestExtensionGroups(classId)?.FirstOrDefault(
            x => string.Equals(x.Id, groupId, StringComparison.Ordinal));

    /// <summary>
    /// 是否可以修改指定班级的扩展设置：系统管理员；或班级自治策略允许、且在该班拥有扩展功能权限的班主任。
    /// 插件端只校验扩展权限位，班主任身份与策略只能在服务端复核。
    /// </summary>
    public async Task<bool> CanEditSettingsAsync(UserProfile user, Guid classId, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        if (!(await access.GetClassSelfServiceAsync(user.Id, user.Role, classId, ct)).CanEditExtensionSettings)
            return false;
        var permissions = await access.GetEffectivePermissionsAsync(user.Id, user.Role, classId, user.GrantedPermissions, ct);
        return permissions.HasFlag(UserPermissions.RunExtensions);
    }

    /// <summary>修改单个班级的扩展设置；调用方须先通过 <see cref="CanEditSettingsAsync"/>。</summary>
    public async Task<CommandResult> ApplyToClassAsync(
        UserProfile user,
        Guid classId,
        string groupId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default)
    {
        var group = FindGroup(classId, groupId);
        if (group is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "当前班级的设备没有上报该扩展分组");
        if (!group.HasSettings)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "该扩展分组没有可修改的设置");
        var normalized = ExtensionFieldValidator.ValidateSettings(group.Settings!, values, out var error);
        if (error is not null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);

        var permissions = await access.GetEffectivePermissionsAsync(user.Id, user.Role, classId, user.GrantedPermissions, ct);
        var result = await peers.SendCommandAndWaitAsync(
            CreateCommand(user.WithPermissions(permissions), groupId, normalized, classId),
            classId,
            CommandTimeout,
            ct);
        return result.Code == CommandResultCodes.PluginOffline
            ? await QueueAsync(user, classId, groupId, normalized, ct)
            : result;
    }

    /// <summary>按批量下发时使用的统一分组定义校验待修改字段；目标设备会再按自身声明复核。</summary>
    public static Dictionary<string, string?> ValidateForBatch(
        ExtensionGroupView group, IReadOnlyDictionary<string, string?> values, out string? error) =>
        ExtensionFieldValidator.ValidateSettings(group.Settings, values, out error);

    public static CommandMessage CreateCommand(
        UserProfile requestedBy, string groupId, IReadOnlyDictionary<string, string?> values, Guid? classId = null) => new()
    {
        Command = CommandKind.ApplyExtensionSettings,
        ClassId = classId,
        RequestedBy = requestedBy,
        ExtensionSettings = new ExtensionSettingsRequest
        {
            GroupId = groupId,
            Values = new Dictionary<string, string?>(values, StringComparer.Ordinal),
        },
    };

    private static IReadOnlyList<ExtensionGroupView> Build(
        IReadOnlyList<ExtensionGroupDefinition> groups, IReadOnlyList<ExtensionDefinition> extensions)
    {
        var known = groups.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var views = groups.Select(group => new ExtensionGroupView(
                group.Id,
                string.IsNullOrWhiteSpace(group.DisplayName) ? group.Id : group.DisplayName,
                group.Description,
                group.Icon,
                group.Settings ?? [],
                group.Values ?? new Dictionary<string, string?>(),
                extensions.Where(x => string.Equals(x.GroupId, group.Id, StringComparison.Ordinal)).ToList()))
            .ToList();
        var ungrouped = extensions.Where(x => x.GroupId is null || !known.Contains(x.GroupId)).ToList();
        if (ungrouped.Count > 0)
            views.Add(new ExtensionGroupView(
                UngroupedId, "其他扩展", "未声明所属插件分组的扩展功能。", null, [],
                new Dictionary<string, string?>(), ungrouped));
        return views;
    }
}

/// <summary>WebUI 中的一个扩展分组（通常对应一个 ClassIsland 插件）。</summary>
public sealed record ExtensionGroupView(
    string Id,
    string DisplayName,
    string? Description,
    string? Icon,
    IReadOnlyList<ExtensionParameter> Settings,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<ExtensionDefinition> Extensions)
{
    public bool HasSettings => Settings.Count > 0;

    public bool IsUngrouped => Id.Length == 0;
}

/// <summary>某个班级对指定分组的上报状态。</summary>
public sealed record ExtensionGroupClassState(
    Guid ClassId,
    string ClassName,
    bool Online,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyDictionary<string, string?>? Pending = null);

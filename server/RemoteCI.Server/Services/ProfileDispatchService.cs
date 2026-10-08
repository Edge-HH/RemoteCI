using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>下发只读取持久化修订版本；不同班级的档案按目标班级路由，绝不使用浏览器草稿。</summary>
public sealed class ProfileDispatchService(ProfileLibraryService library, DeviceInventoryService devices, IdentityCoordinator identities,
    ClassAccessService access, ClassroomService classrooms, PeerRegistry peers)
{
    public async Task<IReadOnlyList<BatchDeviceItemResult>> ApplyAsync(
        AppUser actor, ProfileDispatchRequest input, Guid? onlyClass = null, CancellationToken ct = default)
    {
        if (input.Items.Count is < 1 or > 100 || input.Items.Select(x => x.Id).Distinct().Count() != input.Items.Count)
            throw new ArgumentException("请选择 1 至 100 份已保存的档案，不能重复选择。");
        if (!Enum.IsDefined(input.Mode)) throw new ArgumentException("请明确选择档案应用方式。");
        if (input.Sections == ProfileDistributionSection.None || ((int)input.Sections & ~7) != 0)
            throw new ArgumentException("请至少选择时间表、课表或科目中的一项。");
        if (input.Mode == ProfileApplyMode.ReplaceSections && !input.ConfirmReplace)
            throw new ArgumentException("整体替换会清空所选类别，请先确认。");
        if (input.Mode == ProfileApplyMode.CreateAndActivate && string.IsNullOrWhiteSpace(input.ImportProfileName))
            throw new ArgumentException("请填写要新建并启用的设备档案名。");
        var importName = input.Mode == ProfileApplyMode.CreateAndActivate
            ? ProfileApplyRequest.NormalizeImportName(input.ImportProfileName) : null;

        var sources = new List<StoredProfileDto>();
        foreach (var item in input.Items)
        {
            var row = await library.GetAsync(actor, item.Id, onlyClass, ct);
            if (row.Revision != item.Revision) throw new ProfileRevisionException("所选档案已有新版本，请重新加载后下发。");
            sources.Add(row);
        }
        var unified = sources.Count == 1 && sources[0].ClassId is null;
        if (!unified && sources.Any(x => x.ClassId is null))
            throw new ArgumentException("统一模板下发与各班独立档案下发不能混在同一批次。");

        IReadOnlyCollection<Guid> classIds = onlyClass is { } fixedClass ? [fixedClass] : input.ClassIds;
        IReadOnlyCollection<Guid> groupIds = onlyClass is not null ? [] : input.GroupIds;
        IReadOnlyCollection<Guid> connectionIds = onlyClass is not null ? [] : input.ConnectionIds;
        if (classIds.Count + groupIds.Count + connectionIds.Count == 0)
            throw new ArgumentException("请先选择班级、分组或具体设备。");

        // 完整构建并验证所有载荷后才发送，避免后续一份源档案无效导致半批投递。
        var payloads = sources.ToDictionary(x => x.Id, x => new ProfileApplyRequest
        {
            ProfileJson = ProfileDocument.BuildSelection(x.ProfileJson, input.Sections,
                input.TimeLayoutIds, input.ClassPlanIds, input.SubjectIds),
            Sections = input.Sections,
            Mode = input.Mode,
            ImportProfileName = importName,
            RestartAfter = input.RestartAfter,
        });
        var plan = await devices.ResolveAsync(classIds, groupIds, connectionIds, ct);
        var results = plan.Failures.Select(ToResult).ToList();
        // 班级目标按连接接入顺序选主设备，不能用软件清单刷新时间代替接入时间。
        // 此处局部调整，其他控制功能仍沿用现有 DeviceInventoryService 行为。
        var inventory = await devices.ListAsync(ct);
        var snapshots = peers.GetPluginDeviceSnapshots();
        var chosen = plan.Targets.Where(x => x.ConnectionId is { } id && connectionIds.Contains(id)).ToList();
        foreach (var classId in await classrooms.ResolveTargetClassIdsAsync(classIds.ToList(), groupIds.ToList(), ct))
        {
            var primary = snapshots.FirstOrDefault(x => x.ClassId == classId && x.PluginCredentialId is { } credentialId &&
                inventory.Any(device => device.CredentialId == credentialId));
            if (primary?.PluginCredentialId is not { } primaryCredential) continue;
            var device = inventory.First(x => x.CredentialId == primaryCredential);
            chosen.Add(device with { ConnectionId = primary.ConnectionId, Capabilities = primary.EffectiveCapabilities, Online = true });
        }
        var resolvedTargets = chosen.DistinctBy(x => x.ConnectionId).ToList();
        foreach (var missing in connectionIds.Distinct().Except(resolvedTargets.Select(x => x.ConnectionId ?? Guid.Empty)))
            results.Add(new BatchDeviceItemResult
            {
                TargetId = missing, ConnectionId = missing, TargetName = $"设备连接 {missing}",
                Success = false, Message = "设备连接已断开，请重新选择当前在线设备。",
            });
        var targets = new List<DeviceInventory>();
        var byClass = sources.Where(x => x.ClassId is not null).ToDictionary(x => x.ClassId!.Value);
        foreach (var target in resolvedTargets)
        {
            if (!await library.CanManageClassAsync(actor, target.ClassId, ct)) throw new UnauthorizedAccessException("不能向其他班级下发档案。");
            var source = unified ? sources[0] : byClass.GetValueOrDefault(target.ClassId);
            if (source is null)
                results.Add(Failure(target, "所选班级没有对应的已保存档案。"));
            else if (!target.Capabilities.Contains(RemoteCiCapabilities.ProfileApply))
                results.Add(Failure(target, "当前插件不支持档案管理，请升级 RemoteCI 插件后重试。"));
            else targets.Add(target);
        }

        var identity = await identities.GetProfileAsync(actor.Id, ct)
            ?? throw new UnauthorizedAccessException("账号状态已变化，请重新登录。");
        var targetIdentities = new Dictionary<Guid, UserProfile>();
        foreach (var classId in targets.Select(x => x.ClassId).Distinct())
            targetIdentities[classId] = identity.WithPermissions(await access.GetEffectivePermissionsAsync(
                actor.Id, actor.Role, classId, actor.GrantedPermissions, ct));
        var dispatch = await devices.DispatchAsync(targets, target =>
        {
            var source = unified ? sources[0] : byClass[target.ClassId];
            return new CommandMessage
            {
                Command = CommandKind.ApplyProfile,
                ClassId = target.ClassId,
                ProfileApply = payloads[source.Id],
                RequestedBy = targetIdentities[target.ClassId],
            };
        }, "档案应用", TimeSpan.FromSeconds(20), ct, result => result.Message);
        results.AddRange(dispatch.Select(ToResult));
        return results;
    }

    private static BatchDeviceItemResult ToResult(DeviceCommandResult result) => new()
    {
        TargetId = result.CredentialId, ConnectionId = result.ConnectionId, ClassId = result.ClassId,
        TargetName = result.DeviceName, Success = result.Success, Message = result.Message,
    };
    private static BatchDeviceItemResult Failure(DeviceInventory target, string message) => new()
    {
        TargetId = target.CredentialId, ConnectionId = target.ConnectionId, ClassId = target.ClassId,
        TargetName = target.DeviceName, Success = false, Message = message,
    };
}

public sealed class ProfileDispatchRequest
{
    public List<ProfileIdRequest> Items { get; set; } = [];
    public ProfileApplyMode Mode { get; set; }
    public ProfileDistributionSection Sections { get; set; }
    public List<Guid>? TimeLayoutIds { get; set; }
    public List<Guid>? ClassPlanIds { get; set; }
    public List<Guid>? SubjectIds { get; set; }
    public string? ImportProfileName { get; set; }
    public bool RestartAfter { get; set; }
    public bool ConfirmReplace { get; set; }
    public List<Guid> ClassIds { get; set; } = [];
    public List<Guid> GroupIds { get; set; } = [];
    public List<Guid> ConnectionIds { get; set; } = [];
}

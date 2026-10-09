using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>下发只读取持久化修订版本；不同班级的档案按目标班级路由，绝不使用浏览器草稿。</summary>
public sealed class ProfileDispatchService(ProfileLibraryService library, DeviceInventoryService devices, IdentityCoordinator identities,
    ClassAccessService access, ClassroomService classrooms, PeerRegistry peers)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<BatchDeviceItemResult>> ApplyAsync(
        AppUser actor, ProfileDispatchRequest input, Guid? onlyClass = null, CancellationToken ct = default)
    {
        if (input.Items.Count is < 1 or > 100 || input.Items.Select(x => x.Id).Distinct().Count() != input.Items.Count)
            throw new ArgumentException("请选择 1 至 100 份已保存的档案，不能重复选择。");
        if (!Enum.IsDefined(input.Mode)) throw new ArgumentException("请明确选择档案应用方式。");
        var tempLayers = input.Mode == ProfileApplyMode.TempLayers;
        if (!tempLayers && (input.Sections == ProfileDistributionSection.None || ((int)input.Sections & ~7) != 0))
            throw new ArgumentException("请至少选择时间表、课表或科目中的一项。");
        if (input.Mode == ProfileApplyMode.ReplaceSections && !input.ConfirmReplace)
            throw new ArgumentException("整体替换会清空所选类别，请先确认。");
        if (input.Mode == ProfileApplyMode.CreateAndActivate && string.IsNullOrWhiteSpace(input.ImportProfileName))
            throw new ArgumentException("请填写要新建并启用的设备档案名。");
        if (tempLayers && input.TempLayerIds is { Count: 0 })
            throw new ArgumentException("请至少选择一个临时层。");
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
        // 临时层按班级各不相同，某班档案没有临时层只让该班失败，不阻止其他班级。
        var payloads = new Dictionary<Guid, ProfileApplyRequest>();
        var payloadErrors = new Dictionary<Guid, string>();
        foreach (var source in sources)
        {
            string json;
            if (!tempLayers)
                json = ProfileDocument.BuildSelection(source.ProfileJson, input.Sections,
                    input.TimeLayoutIds, input.ClassPlanIds, input.SubjectIds);
            else
            {
                try { json = ProfileDocument.BuildTempLayerSelection(source.ProfileJson, sources.Count == 1 ? input.TempLayerIds : null); }
                catch (ArgumentException ex) when (sources.Count > 1)
                {
                    payloadErrors[source.Id] = $"档案“{source.Name}”：{ex.Message}";
                    continue;
                }
            }
            payloads[source.Id] = new ProfileApplyRequest
            {
                ProfileJson = json,
                Sections = tempLayers ? ProfileDistributionSection.None : input.Sections,
                Mode = input.Mode,
                ImportProfileName = importName,
                RestartAfter = input.RestartAfter,
                ReplaceExistingTempLayers = tempLayers && input.ReplaceExistingTempLayers,
            };
        }
        var plan = await devices.ResolveAsync(classIds, groupIds, connectionIds, ct);
        var results = plan.Failures.Select(ToResult).ToList();
        // 班级目标按连接接入顺序选主设备，不能用软件清单刷新时间代替接入时间。
        // 此处局部调整，其他控制功能仍沿用现有 DeviceInventoryService 行为。
        var inventory = await devices.ListAsync(ct);
        var snapshots = peers.GetPluginDeviceSnapshots();
        var chosen = plan.Targets.Where(x => x.ConnectionId is { } id && connectionIds.Contains(id)).ToList();
        foreach (var classId in await classrooms.ResolveTargetClassIdsAsync(classIds.ToList(), groupIds.ToList(), ct))
            if (PrimaryDevice(classId, snapshots, inventory) is { } primary) chosen.Add(primary);
        var resolvedTargets = chosen.DistinctBy(x => x.ConnectionId).ToList();
        foreach (var missing in connectionIds.Distinct().Except(resolvedTargets.Select(x => x.ConnectionId ?? Guid.Empty)))
            results.Add(new BatchDeviceItemResult
            {
                TargetId = missing, ConnectionId = missing, TargetName = $"设备连接 {missing}",
                Success = false, Message = "设备连接已断开，请重新选择当前在线设备。",
            });
        var targets = new List<DeviceInventory>();
        var byClass = sources.Where(x => x.ClassId is not null).ToDictionary(x => x.ClassId!.Value);
        var requiredCapability = tempLayers ? RemoteCiCapabilities.ProfileTempLayer : RemoteCiCapabilities.ProfileApply;
        foreach (var target in resolvedTargets)
        {
            if (!await library.CanManageClassAsync(actor, target.ClassId, ct)) throw new UnauthorizedAccessException("不能向其他班级下发档案。");
            var source = unified ? sources[0] : byClass.GetValueOrDefault(target.ClassId);
            if (source is null)
                results.Add(Failure(target, "所选班级没有对应的已保存档案。"));
            else if (payloadErrors.TryGetValue(source.Id, out var payloadError))
                results.Add(Failure(target, payloadError));
            else if (!target.Capabilities.Contains(requiredCapability))
                results.Add(Failure(target, tempLayers
                    ? "当前插件不支持下发临时层，请升级 RemoteCI 插件后重试。"
                    : "当前插件不支持档案管理，请升级 RemoteCI 插件后重试。"));
            else targets.Add(target);
        }

        var targetIdentities = await IdentitiesAsync(actor, targets.Select(x => x.ClassId), ct);
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
        }, "档案应用", CommandTimeout, ct, result => result.Message);
        results.AddRange(dispatch.Select(ToResult));
        return results;
    }

    /// <summary>
    /// 从各班在线设备读取当前档案。结果只返回给调用方作为草稿，不写入档案库；
    /// 成功项已按宿主规则规范化，仍有校验问题时在结果中列出，由用户在编辑器里修正后保存。
    /// </summary>
    public async Task<IReadOnlyList<ProfileCollectResult>> CollectAsync(
        AppUser actor, ProfileCollectRequest input, Guid? onlyClass = null, CancellationToken ct = default)
    {
        List<Guid> classIds = onlyClass is { } fixedClass ? [fixedClass] : input.ClassIds.Distinct().ToList();
        if (classIds.Count is < 1 or > 100) throw new ArgumentException("请选择 1 至 100 个班级收集档案。");
        // 权限与数据库查询在并发发送前串行完成，DbContext 不支持并发访问。
        var manageable = new HashSet<Guid>();
        foreach (var classId in classIds)
            if (await library.CanManageClassAsync(actor, classId, ct)) manageable.Add(classId);
        // 一个班都不能管理时整体拒绝；混有无权班级时逐班报告，且不回显这些班级的名称。
        if (manageable.Count == 0) throw new UnauthorizedAccessException("没有管理所选班级档案的权限。");
        var names = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var inventory = await devices.ListAsync(ct);
        var snapshots = peers.GetPluginDeviceSnapshots();
        var targetIdentities = await IdentitiesAsync(actor, manageable, ct);

        var tasks = classIds.Select(async classId =>
        {
            if (!manageable.Contains(classId))
                return ProfileCollectResult.Failure(classId, "无权访问的班级", null, "没有管理此班级档案的权限。");
            var className = names.GetValueOrDefault(classId) ?? "未知班级";
            if (PrimaryDevice(classId, snapshots, inventory) is not { ConnectionId: { } connectionId } device)
                return ProfileCollectResult.Failure(classId, className, null, "班级设备未在线，无法收集。");
            if (!device.Capabilities.Contains(RemoteCiCapabilities.ProfileRead))
                return ProfileCollectResult.Failure(classId, className, device.DeviceName, "当前插件不支持收集档案，请升级 RemoteCI 插件后重试。");
            var reply = await peers.SendCommandAndWaitToConnectionAsync(new CommandMessage
            {
                Command = CommandKind.ReadProfile,
                ClassId = classId,
                RequestedBy = targetIdentities[classId],
            }, connectionId, CommandTimeout, ct);
            if (!reply.Success)
                return ProfileCollectResult.Failure(classId, className, device.DeviceName, reply.Message);
            try
            {
                var json = ProfileDocument.NormalizeCollected(reply.Data ?? string.Empty);
                var errors = ProfileLibraryService.Preview(json).Errors;
                return new ProfileCollectResult(classId, className, device.DeviceName, true,
                    errors.Count == 0 ? "已收集，请检查后保存。" : $"已收集，保存前需修正 {errors.Count} 项问题。", json, errors);
            }
            catch (ArgumentException ex)
            {
                return ProfileCollectResult.Failure(classId, className, device.DeviceName, $"设备返回的档案无效：{ex.Message}");
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>每班只有一台在线插件；未分配班级的设备在快照中记为默认班级，不能被当成默认班的主设备。</summary>
    private static DeviceInventory? PrimaryDevice(Guid classId, IReadOnlyList<PluginDeviceSnapshot> snapshots,
        IReadOnlyList<DeviceInventory> inventory)
    {
        var primary = snapshots.FirstOrDefault(x => x.Assigned && x.ClassId == classId && x.PluginCredentialId is { } credentialId &&
            inventory.Any(device => device.CredentialId == credentialId));
        if (primary?.PluginCredentialId is not { } primaryCredential) return null;
        var device = inventory.First(x => x.CredentialId == primaryCredential);
        return device with { ConnectionId = primary.ConnectionId, Capabilities = primary.EffectiveCapabilities, Online = true };
    }

    private async Task<Dictionary<Guid, UserProfile>> IdentitiesAsync(AppUser actor, IEnumerable<Guid> classIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, UserProfile>();
        var ids = classIds.Distinct().ToList();
        if (ids.Count == 0) return result;
        var identity = await identities.GetProfileAsync(actor.Id, ct)
            ?? throw new UnauthorizedAccessException("账号状态已变化，请重新登录。");
        foreach (var classId in ids)
            result[classId] = identity.WithPermissions(await access.GetEffectivePermissionsAsync(
                actor.Id, actor.Role, classId, actor.GrantedPermissions, ct));
        return result;
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
    /// <summary>仅 TempLayers 方式：要下发的临时层课表 ID；省略表示档案中全部临时层，多份档案批量下发时忽略。</summary>
    public List<Guid>? TempLayerIds { get; set; }
    public bool ReplaceExistingTempLayers { get; set; }
    public string? ImportProfileName { get; set; }
    public bool RestartAfter { get; set; }
    public bool ConfirmReplace { get; set; }
    public List<Guid> ClassIds { get; set; } = [];
    public List<Guid> GroupIds { get; set; } = [];
    public List<Guid> ConnectionIds { get; set; } = [];
}

public sealed class ProfileCollectRequest
{
    public List<Guid> ClassIds { get; set; } = [];
}

/// <summary>收集结果只作草稿返回；ProfileJson 仅在成功时提供。</summary>
public sealed record ProfileCollectResult(Guid ClassId, string ClassName, string? DeviceName, bool Success, string Message,
    string? ProfileJson, IReadOnlyList<string> Errors)
{
    public static ProfileCollectResult Failure(Guid classId, string className, string? deviceName, string message) =>
        new(classId, className, deviceName, false, message, null, []);

    /// <summary>WebUI 与 REST 共用的汇总文案。</summary>
    public static string Summary(IReadOnlyList<ProfileCollectResult> results) =>
        $"已收集 {results.Count(x => x.Success)} 个班级，失败 {results.Count(x => !x.Success)} 个。收集结果尚未保存。";
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ClassesModel(
    UserManager<AppUser> users,
    ClassroomService classrooms,
    IdentityCoordinator identities,
    AccountRoleService roleService,
    AuthorizationSyncService authorizationSync,
    VisitorAccessSettings visitorAccess,
    ClassExcelService classExcel,
    IMemoryCache reportCache,
    PeerRegistry peers,
    ClassSelfServiceSettings selfService)
    : WebPageModel(users)
{
    /// <summary>虚拟树节点：用于筛选没有任何分组归属的班级。</summary>
    public static readonly Guid UnassignedGroupId = Guid.Empty;

    [BindProperty]
    public string NewClassName { get; set; } = string.Empty;

    /// <summary>新建班级时可选的固定配对码；留空则创建后再单独设置。</summary>
    [BindProperty]
    public string? NewClassPairCode { get; set; }

    /// <summary>班级管理页设置统一连接码时的可选输入；留空表示自动生成。</summary>
    [BindProperty]
    public string? UnifiedPairCodeInput { get; set; }

    /// <summary>为某个班级设置/重置固定配对码时的可选输入；留空表示自动生成。</summary>
    [BindProperty]
    public string? ClassPairCodeInput { get; set; }

    /// <summary>把未分配设备指派到目标班级时提交的班级 Id。</summary>
    [BindProperty]
    public Guid AssignDeviceClassId { get; set; }

    [BindProperty]
    public List<Guid> SelectedClassIds { get; set; } = [];

    [BindProperty]
    public string BatchOperation { get; set; } = string.Empty;

    [BindProperty]
    public string NewGroupName { get; set; } = string.Empty;

    [BindProperty]
    public List<Guid> BatchTargetGroupIds { get; set; } = [];

    /// <summary>当前树中选中的分组；为空表示查看全部班级。</summary>
    [BindProperty(SupportsGet = true, Name = "groupId")]
    public Guid? SelectedGroupId { get; set; }

    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> Groups { get; private set; } = [];
    public IReadOnlyList<ClassGroupTreeNode> GroupTree { get; private set; } = [];
    public IReadOnlyList<ClassDetail> VisibleClasses { get; private set; } = [];
    public ClassGroupInfo? SelectedGroup { get; private set; }
    public bool IsUngroupedSelected => SelectedGroupId == UnassignedGroupId;
    public int UnassignedClassCount => Classes.Count(x => x.GroupIds is not { Count: > 0 });
    public string ScopeTitle => IsUngroupedSelected ? "未分组" : SelectedGroup?.Name ?? "全部班级";
    public string ScopeDescription => SelectedGroup is null
        ? IsUngroupedSelected
            ? "显示尚未归入任何分组的班级；可直接批量加入分组或调整访客。"
            : "显示全部班级；勾选后可批量调整访客、分组或删除。"
        : $"显示“{SelectedGroup.Name}”及其全部子分组中的班级；可直接批量管理。";
    public int OnlineClassCount => VisibleClasses.Count(x => x.PluginCount > 0);
    public int VisitorClassCount => VisibleClasses.Count(x => x.VisitorEnabled);
    public Dictionary<Guid, IReadOnlyList<ClassMemberInfo>> MembersByClass { get; private set; } = new();

    /// <summary>树节点只承载渲染所需的派生信息，避免在 Razor 中重复计算层级与范围。</summary>
    public sealed class ClassGroupTreeNode
    {
        public ClassGroupInfo Group { get; init; } = null!;
        public IReadOnlyList<ClassGroupTreeNode> Children { get; init; } = [];
        public IReadOnlyList<Guid> SubtreeGroupIds { get; init; } = [];
        public IReadOnlyList<ClassGroupInfo> ParentCandidates { get; init; } = [];
        public int TotalClassCount { get; init; }
        public bool IsSelected { get; init; }
    }
    public IReadOnlyList<UserListItem> Accounts { get; private set; } = [];
    public IReadOnlyList<AccountRoleInfo> RoleDefinitions { get; private set; } = [];
    public bool AnyVisitorClass { get; private set; }
    public bool AutoEnter { get; private set; }

    /// <summary>班级自治策略：系统管理员统一决定班主任可在本班自行完成的操作。</summary>
    public ClassSelfServicePolicy SelfServicePolicy { get; private set; } = ClassSelfServicePolicy.Default;

    [BindProperty]
    public SelfServiceInput SelfServiceOptions { get; set; } = new();

    public sealed class SelfServiceInput
    {
        public bool CanRename { get; set; }
        public bool CanChangeAvatar { get; set; }
        public bool CanPullSchedule { get; set; }
        public bool CanEditExtensionSettings { get; set; }
    }

    /// <summary>是否已设置统一连接码；不展示明文。</summary>
    public bool HasUnifiedCode { get; private set; }

    /// <summary>使用统一连接码接入、尚未分配班级的设备。</summary>
    public IReadOnlyList<UnassignedDevice> UnassignedDevices { get; private set; } = [];

    /// <summary>一次性生成的固定配对码明文，仅本次响应展示一次。</summary>
    public string? GeneratedPairCode { get; private set; }

    /// <summary>刚生成的固定码属于哪个班级，用于在对应行高亮提示。</summary>
    public Guid? GeneratedPairCodeClassId { get; private set; }

    public sealed record UnassignedDevice(
        Guid CredentialId, string DeviceName, string? Remark, bool Online, DateTimeOffset LastSeenAt);

    /// <summary>最近一次 Excel 导入结果；完整失败列表通过短时缓存跨重定向传递。</summary>
    public ClassExcelImportResult? ExcelImportResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var created = await classrooms.CreateAsync(NewClassName, ct);
            // 新建时可一并指定固定配对码；留空则先建班，之后在行内单独设置。
            if (!string.IsNullOrWhiteSpace(NewClassPairCode))
            {
                await identities.SetClassPairingCodeAsync(created.Id, NewClassPairCode, ct);
                TempData["Message"] = "班级已创建并设置固定配对码。";
            }
            else
            {
                TempData["Message"] = "班级已创建。";
            }
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>
    /// 设置统一连接码：使用该码接入的设备可无限连接，但一律进入“未分配”，
    /// 需管理员在本页指派班级后才能参与该班的数据与控制。
    /// </summary>
    public async Task<IActionResult> OnPostSetUnifiedCodeAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var code = await identities.CreateSharedPluginPairingCodeAsync(UnifiedPairCodeInput, ct);
            TempData["Message"] = $"统一连接码已更新：{code}（仅本次显示，请立即记录）。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>设置或重置某个班级的固定配对码；生成的明文只在本次响应展示一次。</summary>
    public async Task<IActionResult> OnPostSetClassPairCodeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            GeneratedPairCode = await identities.SetClassPairingCodeAsync(id, ClassPairCodeInput, ct);
            GeneratedPairCodeClassId = id;
            TempData["Message"] = "班级固定配对码已设置，插件使用该码连接后会自动绑定到本班。";
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToClasses();
        }
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>把未分配设备指派到班级；指派后立即推送该班授权镜像，无需插件重连。</summary>
    public async Task<IActionResult> OnPostAssignDeviceAsync(Guid credentialId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        if (AssignDeviceClassId == Guid.Empty)
        {
            TempData["Error"] = "请选择要指派到的班级。";
            return RedirectToClasses();
        }
        try
        {
            await identities.AssignPluginCredentialAsync(credentialId, AssignDeviceClassId, ct);
            var sync = await identities.CreateSyncAsync(AssignDeviceClassId, ct);
            await peers.ReassignPluginConnectionsAsync(credentialId, AssignDeviceClassId, sync, ct);
            TempData["Message"] = "设备已指派到班级。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>吊销未分配设备的凭据，清理误接入或废弃的连接。</summary>
    public async Task<IActionResult> OnPostRevokeUnassignedAsync(Guid credentialId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await identities.RevokePluginCredentialAsync(credentialId, ct);
            await peers.DisconnectPluginCredentialAsync(credentialId, ct);
            TempData["Message"] = "未分配设备已移除。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id, string name, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.RenameAsync(id, name, ct);
            TempData["Message"] = "班级名称已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.DeleteAsync(id, ct);
            await peersDisconnect(id, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "班级已删除，成员关系与插件凭据一并移除。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>保存班级自治策略；只影响服务端复核，不需要刷新插件授权镜像。</summary>
    public async Task<IActionResult> OnPostSaveSelfServiceAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        await selfService.SetAsync(new ClassSelfServicePolicy(
            SelfServiceOptions.CanRename,
            SelfServiceOptions.CanChangeAvatar,
            SelfServiceOptions.CanPullSchedule,
            SelfServiceOptions.CanEditExtensionSettings), ct);
        TempData["Message"] = "班主任权限已保存，对全部班级生效。";
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostToggleVisitorAsync(Guid id, bool enabled, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.SetVisitorAccessAsync(id, enabled, ct);
            TempData["Message"] = enabled ? "已开启该班级的访客功能。" : "已关闭该班级的访客功能。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostAddMemberAsync(Guid id, Guid userId, Guid roleId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var members = (await classrooms.ListMembersAsync(id, ct)).ToList();
            if (members.All(x => x.UserId != userId))
                members.Add(new ClassMemberInfo { UserId = userId, RoleId = roleId });
            await classrooms.UpdateMembersAsync(id, new UpdateClassMembersRequest
            {
                Members = members.Select(x => new ClassMemberInput { UserId = x.UserId, RoleId = x.RoleId }).ToList(),
            }, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "成员已添加。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(new { classId = (Guid?)id, groupId = SelectedGroupId });
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(Guid id, Guid userId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var members = (await classrooms.ListMembersAsync(id, ct)).ToList();
            await classrooms.UpdateMembersAsync(id, new UpdateClassMembersRequest
            {
                Members = members.Where(x => x.UserId != userId)
                    .Select(x => new ClassMemberInput { UserId = x.UserId, RoleId = x.RoleId }).ToList(),
            }, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "成员已移出班级。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(new { classId = (Guid?)id, groupId = SelectedGroupId });
    }

    public async Task<IActionResult> OnPostBatchAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        if (SelectedClassIds.Count == 0)
        {
            TempData["Error"] = "请先勾选要操作的班级。";
            return RedirectToClasses();
        }

        var operation = BatchOperation.Trim();
        var isGroupOperation = IsBatchGroupOperation(operation);
        try
        {
            if (ValidateBatchTarget(operation, isGroupOperation) is { } error)
            {
                TempData["Error"] = error;
                return RedirectToClasses();
            }
            var result = await ExecuteBatchAsync(operation, isGroupOperation, ct);
            await StoreBatchResultAsync(operation, isGroupOperation, result, ct);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToClasses();
        }
        return RedirectToClasses();
    }

    private string? ValidateBatchTarget(string operation, bool isGroupOperation) =>
        isGroupOperation && !operation.Equals("clearGroups", StringComparison.OrdinalIgnoreCase) && BatchTargetGroupIds.Count == 0
            ? "请选择要批量调整到的目标分组。"
            : null;

    private Task<BatchClassOperationResult> ExecuteBatchAsync(string operation, bool isGroupOperation, CancellationToken ct) =>
        isGroupOperation
            ? classrooms.BatchSetClassGroupsAsync(SelectedClassIds, BatchTargetGroupIds, operation, ct)
            : classrooms.BatchAsync(new BatchClassOperationRequest
            {
                ClassIds = SelectedClassIds,
                Operation = operation,
            }, ct);

    private async Task StoreBatchResultAsync(
        string operation,
        bool isGroupOperation,
        BatchClassOperationResult result,
        CancellationToken ct)
    {
        var succeeded = result.Results.Count(x => x.Success);
        if (succeeded > 0)
        {
            if (operation.Equals("delete", StringComparison.OrdinalIgnoreCase))
                foreach (var item in result.Results.Where(x => x.Success))
                    await peersDisconnect(item.ClassId, ct);
            if (!isGroupOperation)
                await authorizationSync.SyncAsync(ct);
        }

        var failures = result.Results.Where(x => !x.Success).ToList();
        var operationName = isGroupOperation ? "批量分组调整" : "批量操作";
        TempData[succeeded > 0 ? "Message" : "Error"] = failures.Count == 0
            ? $"{operationName}已完成（{succeeded} 个班级）。"
            : $"{operationName}完成 {succeeded} 个，失败 {failures.Count} 个：{string.Join("；", failures.Select(x => x.Message))}";
    }

    public async Task<IActionResult> OnPostCreateGroupAsync(Guid? parentGroupId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var created = await classrooms.CreateGroupAsync(NewGroupName, parentGroupId, ct);
            TempData["Message"] = "分组已创建。";
            return RedirectToPage(new { groupId = created.Id });
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToClasses();
        }
    }

    public async Task<IActionResult> OnPostRenameGroupAsync(Guid id, string name, Guid? parentGroupId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.RenameGroupAsync(id, name, parentGroupId, ct);
            TempData["Message"] = "分组已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>整体替换一个分组包含的班级。</summary>
    public async Task<IActionResult> OnPostSetGroupClassesAsync(Guid id, List<Guid> classIds, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.SetGroupClassesAsync(id, classIds ?? [], ct);
            TempData["Message"] = "分组成员班级已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    /// <summary>整体替换一个班级所属的分组（多归属）。</summary>
    public async Task<IActionResult> OnPostSetClassGroupsAsync(Guid id, List<Guid> groupIds, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.SetClassGroupsAsync(id, groupIds ?? [], ct);
            TempData["Message"] = "班级分组已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostDeleteGroupAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.DeleteGroupAsync(id, ct);
            TempData["Message"] = "分组已删除，子分组上移一级，班级本身不受影响。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToClasses();
    }

    // ---------- Excel 批量导入 / 导出 ----------

    public async Task<IActionResult> OnGetTemplateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var content = await classExcel.CreateTemplateAsync(ct);
        return File(content, ClassExcelService.ExcelContentType, ClassExcelService.TemplateFileName);
    }

    public async Task<IActionResult> OnGetExportAsync(Guid? groupId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var export = await classExcel.ExportAsync(groupId, ct);
            return File(export.Content, ClassExcelService.ExcelContentType, export.FileName);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToClasses();
        }
    }

    public async Task<IActionResult> OnPostImportExcelAsync(IFormFile? excelFile, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var bytes = await ReadExcelFileAsync(excelFile, ct);
        if (bytes is null) return RedirectToClasses();
        try
        {
            using var stream = new MemoryStream(bytes);
            var result = await classExcel.ImportAsync(stream, ct);
            StoreExcelResult(result);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToClasses();
    }

    public async Task<IActionResult> OnPostOverwriteExcelAsync(IFormFile? excelFile, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var bytes = await ReadExcelFileAsync(excelFile, ct);
        if (bytes is null) return RedirectToClasses();
        try
        {
            using var stream = new MemoryStream(bytes);
            var result = await classExcel.OverwriteAsync(stream, ct);
            StoreExcelResult(result);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToClasses();
    }

    /// <summary>把完整导入结果放进短时缓存，TempData 只保存一个很小的键，避免 Cookie 超限。</summary>
    private void StoreExcelResult(ClassExcelImportResult result)
    {
        var key = Guid.NewGuid().ToString("N");
        reportCache.Set(key, result, TimeSpan.FromMinutes(10));
        TempData["ClassExcelReportId"] = key;
        TempData[result.HasFailures ? "Error" : "Message"] = result.Summary;
    }

    private async Task<byte[]?> ReadExcelFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "请选择要上传的 .xlsx 文件。";
            return null;
        }
        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "Excel 文件不能超过 20 MB。";
            return null;
        }
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "仅支持 .xlsx 格式的 Excel 文件。";
            return null;
        }
        using var buffer = new MemoryStream();
        await using var stream = file.OpenReadStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Classes = await classrooms.ListAsync(ct);
        Groups = await classrooms.ListGroupsAsync(ct);
        Accounts = await identities.ListUsersAsync(ct);
        RoleDefinitions = await roleService.ListAsync(ct);
        foreach (var classroom in Classes)
            MembersByClass[classroom.Id] = await classrooms.ListMembersAsync(classroom.Id, ct);
        AnyVisitorClass = await visitorAccess.AnyVisitorClassEnabledAsync(ct);
        AutoEnter = await visitorAccess.GetAutoEnterAsync(ct);
        HasUnifiedCode = await identities.HasSharedPluginPairingCodeAsync(ct);
        SelfServicePolicy = await selfService.GetAsync(ct);
        UnassignedDevices = await LoadUnassignedDevicesAsync(ct);
        if (TempData["ClassExcelReportId"] is string reportId &&
            reportCache.TryGetValue(reportId, out ClassExcelImportResult? report))
        {
            ExcelImportResult = report;
        }

        ResolveSelectionState();
    }

    private async Task<List<UnassignedDevice>> LoadUnassignedDevicesAsync(CancellationToken ct)
    {
        // 未分配设备把凭据（含班级名备注）与在线设备名合并展示，离线时仍能看到最后状态。
        var unassigned = await identities.ListUnassignedPluginCredentialsAsync(ct);
        var onlineByName = peers.GetPluginDeviceSnapshots()
            .Where(x => !x.Assigned && x.PluginCredentialId is not null)
            .GroupBy(x => x.PluginCredentialId!.Value)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(x => x.SoftwareInventoryAt ?? DateTimeOffset.MinValue).First());
        return unassigned.Select(credential =>
        {
            onlineByName.TryGetValue(credential.Id, out var snapshot);
            return new UnassignedDevice(credential.Id, snapshot?.DisplayName ?? credential.Name,
                credential.ClassNameRemark, snapshot is not null, credential.LastSeenAt);
        }).ToList();
    }

    private void ResolveSelectionState()
    {
        // 无效的 query 值不应让页面停留在“已选中但无内容”的状态。
        if (SelectedGroupId is { } selectedId && selectedId != UnassignedGroupId && Groups.All(x => x.Id != selectedId))
            SelectedGroupId = null;
        SelectedGroup = SelectedGroupId is { } id && id != UnassignedGroupId ? Groups.First(x => x.Id == id) : null;
        GroupTree = BuildGroupTree(Groups, SelectedGroupId);
        VisibleClasses = ResolveVisibleClasses(Classes, GroupTree, SelectedGroupId);
    }

    private IActionResult RedirectToClasses() => RedirectToPage(new { groupId = SelectedGroupId });

    private static bool IsBatchGroupOperation(string operation) =>
        operation.Equals("addGroups", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("removeGroups", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("replaceGroups", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("clearGroups", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ClassGroupTreeNode> BuildGroupTree(
        IReadOnlyList<ClassGroupInfo> groups, Guid? selectedGroupId)
    {
        var childrenByParent = groups
            .GroupBy(x => x.ParentId ?? Guid.Empty)
            .ToDictionary(x => x.Key, x => x.OrderBy(group => group.Name, StringComparer.CurrentCulture).ToList());
        var knownIds = groups.Select(x => x.Id).ToHashSet();

        ClassGroupTreeNode Build(ClassGroupInfo group, HashSet<Guid> path)
        {
            // 数据库外键会阻止正常环，但损坏数据也不应让页面递归溢出。
            if (!path.Add(group.Id))
                return new ClassGroupTreeNode { Group = group, IsSelected = group.Id == selectedGroupId };

            var children = childrenByParent.GetValueOrDefault(group.Id)?
                .Where(child => !path.Contains(child.Id))
                .Select(child => Build(child, path))
                .ToList() ?? [];
            path.Remove(group.Id);

            var subtreeGroupIds = new List<Guid> { group.Id };
            foreach (var child in children)
                subtreeGroupIds.AddRange(child.SubtreeGroupIds);

            return new ClassGroupTreeNode
            {
                Group = group,
                Children = children,
                SubtreeGroupIds = subtreeGroupIds,
                ParentCandidates = groups,
                TotalClassCount = group.ClassCount + children.Sum(x => x.TotalClassCount),
                IsSelected = group.Id == selectedGroupId,
            };
        }

        var roots = groups
            .Where(group => group.ParentId is null || !knownIds.Contains(group.ParentId.Value))
            .OrderBy(group => group.Name, StringComparer.CurrentCulture)
            .ToList();
        return roots.Select(group => Build(group, [])).ToList();
    }

    private static IReadOnlyList<ClassDetail> ResolveVisibleClasses(
        IReadOnlyList<ClassDetail> classes, IReadOnlyList<ClassGroupTreeNode> tree, Guid? selectedGroupId)
    {
        if (selectedGroupId is not { } id)
            return classes;

        if (id == UnassignedGroupId)
            return classes.Where(classroom => classroom.GroupIds is not { Count: > 0 }).ToList();

        var node = FindGroupNode(tree, id);
        if (node is null)
            return classes;

        var groupIds = node.SubtreeGroupIds.ToHashSet();
        return classes
            .Where(classroom => classroom.GroupIds is { Count: > 0 } && classroom.GroupIds.Any(groupIds.Contains))
            .ToList();
    }

    private static ClassGroupTreeNode? FindGroupNode(IReadOnlyList<ClassGroupTreeNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Group.Id == id)
                return node;
            var child = FindGroupNode(node.Children, id);
            if (child is not null)
                return child;
        }
        return null;
    }

    private async Task peersDisconnect(Guid classId, CancellationToken ct)
    {
        // 班级删除后其插件凭据已被级联清理，主动断开对应在线连接。
        var registry = HttpContext.RequestServices.GetRequiredService<PeerRegistry>();
        await registry.DisconnectPluginClassAsync(classId, ct);
    }
}

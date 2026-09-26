using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
    VisitorAccessSettings visitorAccess)
    : WebPageModel(users)
{
    [BindProperty]
    public string NewClassName { get; set; } = string.Empty;

    [BindProperty]
    public List<Guid> SelectedClassIds { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedGroupIds { get; set; } = [];

    [BindProperty]
    public string BatchOperation { get; set; } = string.Empty;

    [BindProperty]
    public string NewGroupName { get; set; } = string.Empty;

    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> Groups { get; private set; } = [];
    public Dictionary<Guid, IReadOnlyList<ClassMemberInfo>> MembersByClass { get; private set; } = new();
    public IReadOnlyList<UserListItem> Accounts { get; private set; } = [];
    public IReadOnlyList<AccountRoleInfo> RoleDefinitions { get; private set; } = [];
    public bool AnyVisitorClass { get; private set; }
    public bool AutoEnter { get; private set; }

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
            await classrooms.CreateAsync(NewClassName, ct);
            TempData["Message"] = "班级已创建。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage(new { classId = (Guid?)id });
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
        return RedirectToPage(new { classId = (Guid?)id });
    }

    public async Task<IActionResult> OnPostBatchAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        if (SelectedClassIds.Count == 0)
        {
            TempData["Error"] = "请先勾选要操作的班级。";
            return RedirectToPage();
        }
        var result = await classrooms.BatchAsync(
            new BatchClassOperationRequest
            {
                ClassIds = SelectedClassIds,
                GroupIds = SelectedGroupIds,
                Operation = BatchOperation,
            }, ct);
        var succeeded = result.Results.Count(x => x.Success);
        if (succeeded > 0)
        {
            if (BatchOperation.Equals("delete", StringComparison.OrdinalIgnoreCase))
                foreach (var item in result.Results.Where(x => x.Success))
                    await peersDisconnect(item.ClassId, ct);
            await authorizationSync.SyncAsync(ct);
        }
        var failures = result.Results.Where(x => !x.Success).ToList();
        TempData[succeeded > 0 ? "Message" : "Error"] = failures.Count == 0
            ? $"批量操作已完成（{succeeded} 个班级）。"
            : $"批量操作完成 {succeeded} 个，失败 {failures.Count} 个：{string.Join("；", failures.Select(x => x.Message))}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateGroupAsync(Guid? parentGroupId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            await classrooms.CreateGroupAsync(NewGroupName, parentGroupId, ct);
            TempData["Message"] = "分组已创建。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
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
    }

    private async Task peersDisconnect(Guid classId, CancellationToken ct)
    {
        // 班级删除后其插件凭据已被级联清理，主动断开对应在线连接。
        var registry = HttpContext.RequestServices.GetRequiredService<PeerRegistry>();
        await registry.DisconnectPluginClassAsync(classId, ct);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 班级区的“扩展插件”页：只查看并修改当前班级的插件设置，不提供应用范围选择。
/// 系统管理员可管理本班全部插件；班主任只能管理系统管理员开放给班级自行管理的插件。
/// 跨班级、按设备统一下发在管理区的“插件批量设置”中进行。
/// </summary>
[Authorize]
public sealed class ClassExtensionsModel(
    UserManager<AppUser> users,
    ExtensionGroupService extensionGroups,
    PendingExtensionSettingsService pending,
    IdentityCoordinator identities,
    PeerRegistry peers) : WebPageModel(users)
{
    [BindProperty(SupportsGet = true)]
    public string? GroupId { get; set; }

    [BindProperty]
    public List<ExtensionFieldInput> SettingInputs { get; set; } = [];

    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;

    /// <summary>当前账号在当前班级可自行管理的插件；系统管理员为 null，表示不受限。</summary>
    public IReadOnlySet<string>? EditableGroupIds { get; private set; }

    /// <summary>本班设备上报、带设置字段且当前账号可以管理的插件。</summary>
    public IReadOnlyList<ExtensionGroupView> Groups { get; private set; } = [];

    public ExtensionGroupView? Group { get; private set; }

    /// <summary>本班离线期间已保存、等待插件上线后写入的设置。</summary>
    public IReadOnlyDictionary<string, string?>? PendingValues { get; private set; }

    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>保存到当前班级：提交表单中的全部字段，由本班插件逐项写入；插件离线时排队补发。</summary>
    public async Task<IActionResult> OnPostApplyAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(GroupId) || !CanEdit(GroupId)) return RedirectToPage("/Denied");
        var profile = await identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return RedirectToPage("/Login");

        var result = await extensionGroups.ApplyToClassAsync(
            profile, CurrentClassId, GroupId, ExtensionFieldInput.ToValues(SettingInputs), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Success
            ? $"已保存到 {CurrentClass?.Name ?? "当前班级"}：{result.Message}"
            : result.Message;
        return RedirectToPage(new { groupId = GroupId });
    }

    /// <summary>与 ExtensionGroupService.CanEditSettingsAsync 口径一致：系统管理员，或该插件已开放且有扩展功能权限的班主任。</summary>
    private bool CanEdit(string groupId) => EditableGroupIds is null
        ? ClassPermissions.HasFlag(UserPermissions.RunExtensions)
        : EditableGroupIds.Contains(groupId);

    /// <summary>需要有当前班级，并且是系统管理员或至少有一个已开放插件可管理的本班班主任。</summary>
    private async Task<IActionResult?> RequireEditorAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentClass is null) return RedirectToPage("/Denied");
        EditableGroupIds = await extensionGroups.ListClassAdminEditableAsync(
            CurrentUser.Id, CurrentUser.Role, CurrentUser.GrantedPermissions, CurrentClassId, HttpContext.RequestAborted);
        return EditableGroupIds is null || EditableGroupIds.Count > 0 ? null : RedirectToPage("/Denied");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Groups = extensionGroups.BuildForClass(CurrentClassId)
            .Where(x => x.HasSettings && CanEdit(x.Id))
            .ToList();
        if (string.IsNullOrWhiteSpace(GroupId)) return;
        Group = Groups.FirstOrDefault(x => string.Equals(x.Id, GroupId, StringComparison.Ordinal));
        if (Group is null) return;
        PendingValues = (await pending.ListByGroupAsync(Group.Id, ct)).GetValueOrDefault(CurrentClassId);
    }
}

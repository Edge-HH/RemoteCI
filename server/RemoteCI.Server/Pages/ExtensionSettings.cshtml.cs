using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 扩展插件设置页：插件通过扩展分组声明的设置字段在这里统一渲染。
/// 班主任只能看到并修改系统管理员逐个开放给班级自行管理的插件，且只作用于当前班级；
/// 系统管理员还可以按班级或分组批量下发、对比各班当前值，并决定每个插件是否开放给班主任。
/// </summary>
[Authorize]
public sealed class ExtensionSettingsModel(
    UserManager<AppUser> users,
    ExtensionGroupService extensionGroups,
    ClassroomService classrooms,
    DeviceInventoryService devices,
    IdentityCoordinator identities,
    ExtensionGroupPolicyService groupPolicies) : WebPageModel(users)
{
    private const string ResultsKey = "ExtensionSettingsResults";
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(20);

    [BindProperty(SupportsGet = true)]
    public string? GroupId { get; set; }

    [BindProperty]
    public List<ExtensionFieldInput> SettingInputs { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedClassIds { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedGroupIds { get; set; } = [];

    [BindProperty]
    public bool AllowClassAdmin { get; set; }

    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;

    /// <summary>当前账号在当前班级可自行管理的插件；系统管理员为 null，表示不受限。</summary>
    public IReadOnlySet<string>? EditableGroupIds { get; private set; }

    /// <summary>已开放给班主任自行管理的插件，供系统管理员查看与切换。</summary>
    public IReadOnlySet<string> ClassAdminAllowedGroupIds { get; private set; } = new HashSet<string>();

    /// <summary>与 ExtensionGroupService.CanEditSettingsAsync 口径一致：系统管理员，或该插件已开放且有扩展功能权限的班主任。</summary>
    public bool CanEditCurrentClass => GroupId is not null && (EditableGroupIds is null
        ? ClassPermissions.HasFlag(UserPermissions.RunExtensions)
        : EditableGroupIds.Contains(GroupId));

    /// <summary>带设置页的扩展分组：管理员看全部班级的并集，班主任只看当前班级中已开放的插件。</summary>
    public IReadOnlyList<ExtensionGroupView> Groups { get; private set; } = [];

    public ExtensionGroupView? Group { get; private set; }

    /// <summary>当前班级设备上报的该分组（含当前值）；为 null 表示当前班级没有安装该插件或插件离线后未同步。</summary>
    public ExtensionGroupDefinition? CurrentClassGroup { get; private set; }

    public IReadOnlyList<ExtensionGroupClassState> ClassStates { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> ClassGroups { get; private set; } = [];
    public IReadOnlyList<SettingsDispatchResult> LastResults { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        await LoadAsync(ct);
        RestoreResults();
        return Page();
    }

    /// <summary>修改当前班级：提交表单中的全部字段，由插件逐项写入。</summary>
    public async Task<IActionResult> OnPostApplyCurrentAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(GroupId) || !CanEditCurrentClass) return RedirectToPage("/Denied");
        var profile = await identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return RedirectToPage("/Login");

        var result = await extensionGroups.ApplyToClassAsync(
            profile, CurrentClassId, GroupId, ExtensionFieldInput.ToValues(SettingInputs), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Success
            ? $"已保存到 {CurrentClass?.Name ?? "当前班级"}：{result.Message}"
            : result.Message;
        return RedirectToPage(new { groupId = GroupId });
    }

    /// <summary>
    /// 系统管理员批量下发：只发送勾选了“修改此项”的字段，未勾选的字段保持各班原值；
    /// 选中班级内的每一台在线设备都会收到，保证同班多设备设置一致。
    /// </summary>
    public async Task<IActionResult> OnPostApplyBatchAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        if (!IsAdmin || string.IsNullOrWhiteSpace(GroupId)) return RedirectToPage("/Denied");
        if (SelectedClassIds.Count == 0 && SelectedGroupIds.Count == 0)
            return Back("请先选择要下发的班级或分组。");

        var group = (await extensionGroups.BuildForAllClassesAsync(ct: ct))
            .FirstOrDefault(x => string.Equals(x.Id, GroupId, StringComparison.Ordinal) && x.HasSettings);
        if (group is null) return Back("扩展插件不存在或尚未同步设置。");
        var values = ExtensionGroupService.ValidateForBatch(
            group, ExtensionFieldInput.ToValues(SettingInputs, onlyApplied: true), out var error);
        if (error is not null) return Back(error);

        var profile = await identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return RedirectToPage("/Login");
        var classIds = await classrooms.ResolveTargetClassIdsAsync(SelectedClassIds, SelectedGroupIds, ct);
        var classNames = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var allDevices = await devices.ListAsync(ct);
        var results = new List<SettingsDispatchResult>();
        var targets = new List<DeviceInventory>();
        foreach (var classId in classIds)
        {
            var online = allDevices
                .Where(x => x.Assigned && x.ClassId == classId && x.Online && x.ConnectionId is not null)
                .ToList();
            if (online.Count == 0)
            {
                // 离线班级保存为待补发，插件上线同步扩展分组后由服务端自动写入。
                var queued = await extensionGroups.QueueAsync(profile, classId, group.Id, values, ct);
                results.Add(new SettingsDispatchResult(classNames.GetValueOrDefault(classId, "未知班级"), "-", true, queued.Message));
            }
            targets.AddRange(online);
        }

        var dispatched = await devices.DispatchAsync(
            targets,
            device => ExtensionGroupService.CreateCommand(profile, group.Id, values, device.ClassId),
            "扩展设置",
            BatchTimeout,
            ct,
            result => result.Message);
        results.AddRange(dispatched.Select(x => new SettingsDispatchResult(
            classNames.GetValueOrDefault(x.ClassId, "未知班级"), x.DeviceName, x.Success, x.Message)));

        TempData[ResultsKey] = JsonSerializer.Serialize(results, JsonDefaults.Options);
        var ok = results.Count(x => x.Success);
        TempData[ok > 0 ? "Message" : "Error"] = ok == results.Count
            ? $"已下发到 {ok} 台设备。"
            : $"下发完成 {ok} 台，失败 {results.Count - ok} 项，详见下方结果。";
        return RedirectToPage(new { groupId = GroupId });
    }

    /// <summary>系统管理员切换该插件是否允许各班班主任自行管理本班设置。</summary>
    public async Task<IActionResult> OnPostClassAdminAccessAsync(CancellationToken ct)
    {
        if (await RequireEditorAsync() is { } denied) return denied;
        if (!IsAdmin || !ExtensionGroupPolicyService.IsValidGroupId(GroupId)) return RedirectToPage("/Denied");
        await groupPolicies.SetClassAdminAllowedAsync(GroupId!, AllowClassAdmin, ct);
        TempData["Message"] = AllowClassAdmin
            ? "已允许各班班主任自行管理此插件。"
            : "已收回班主任的管理权限，此插件只由系统管理员统一管理。";
        return RedirectToPage(new { groupId = GroupId });
    }

    /// <summary>列表与表格中的友好显示：开关显示开启/关闭，候选项显示其显示名称。</summary>
    public static string FormatValue(ExtensionParameter field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        if (field.Type == ExtensionParameterType.Switch)
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "开启" : "关闭";
        if (field.Type == ExtensionParameterType.Select && field.Options is { } options)
        {
            var index = options.IndexOf(value);
            if (index >= 0) return field.OptionLabel(index);
        }
        return value;
    }

    /// <summary>某字段在各班中出现最多的值，用于标出与多数班级不一致的设置。</summary>
    public string? MajorityValue(string key) => ClassStates
        .Where(x => x.Values.Count > 0)
        .Select(x => x.Values.GetValueOrDefault(key))
        .GroupBy(x => x)
        .OrderByDescending(x => x.Count())
        .Select(x => x.Key)
        .FirstOrDefault();

    /// <summary>系统管理员，或至少有一个已开放插件可管理的本班班主任。</summary>
    private async Task<IActionResult?> RequireEditorAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        EditableGroupIds = await extensionGroups.ListClassAdminEditableAsync(
            CurrentUser.Id, CurrentUser.Role, CurrentUser.GrantedPermissions, CurrentClassId, HttpContext.RequestAborted);
        return EditableGroupIds is null || EditableGroupIds.Count > 0 ? null : RedirectToPage("/Denied");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Groups = (IsAdmin
                ? await extensionGroups.BuildForAllClassesAsync(ct: ct)
                : extensionGroups.BuildForClass(CurrentClassId).Where(x => EditableGroupIds!.Contains(x.Id)))
            .Where(x => x.HasSettings)
            .ToList();
        if (IsAdmin) ClassAdminAllowedGroupIds = await groupPolicies.ListClassAdminAllowedAsync(ct);
        if (string.IsNullOrWhiteSpace(GroupId)) return;

        Group = Groups.FirstOrDefault(x => string.Equals(x.Id, GroupId, StringComparison.Ordinal));
        CurrentClassGroup = extensionGroups.FindGroup(CurrentClassId, GroupId);
        if (!IsAdmin || Group is null) return;
        ClassStates = await extensionGroups.ListClassStatesAsync(GroupId, ct);
        ClassGroups = await classrooms.ListGroupsAsync(ct);
    }

    private IActionResult Back(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage(new { groupId = GroupId });
    }

    private void RestoreResults()
    {
        if (TempData[ResultsKey] is not string json) return;
        try
        {
            LastResults = JsonSerializer.Deserialize<List<SettingsDispatchResult>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            LastResults = [];
        }
    }

    public sealed record SettingsDispatchResult(string ClassName, string DeviceName, bool Success, string Message);
}

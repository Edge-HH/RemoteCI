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
/// 管理区的“插件批量设置”页（仅系统管理员）：插件通过扩展分组声明的设置字段在这里统一渲染，
/// 修改按所选范围下发——按班级/分组下发到班内每一台设备，或直接勾选具体设备——而不是当前选中的班级；
/// 同时对比各班当前值，并决定每个插件是否开放给班主任自行管理。
/// 只修改当前班级请使用班级区的“扩展插件”页（/ClassExtensions）。
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

    /// <summary>按具体设备下发时勾选的在线连接。</summary>
    [BindProperty]
    public List<Guid> SelectedConnectionIds { get; set; } = [];

    [BindProperty]
    public bool AllowClassAdmin { get; set; }

    /// <summary>已开放给班主任自行管理的插件，供系统管理员查看与切换。</summary>
    public IReadOnlySet<string> ClassAdminAllowedGroupIds { get; private set; } = new HashSet<string>();

    /// <summary>全部班级设备上报的带设置页的扩展分组并集。</summary>
    public IReadOnlyList<ExtensionGroupView> Groups { get; private set; } = [];

    public ExtensionGroupView? Group { get; private set; }

    public IReadOnlyList<ExtensionGroupClassState> ClassStates { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> ClassGroups { get; private set; } = [];

    /// <summary>上报了该插件的班级中的设备，供“按具体设备”下发。</summary>
    public IReadOnlyList<DeviceInventory> Devices { get; private set; } = [];

    public IReadOnlyList<SettingsDispatchResult> LastResults { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadAsync(ct);
        RestoreResults();
        return Page();
    }

    /// <summary>
    /// 按所选范围下发：只发送勾选了“修改此项”的字段，未勾选的字段保持各设备原值。
    /// 选中班级或分组时班内每一台在线设备都会收到，离线班级保存为待补发；勾选具体设备时只发给这些设备。
    /// </summary>
    public async Task<IActionResult> OnPostApplyBatchAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(GroupId)) return RedirectToPage("/Denied");
        if (SelectedClassIds.Count == 0 && SelectedGroupIds.Count == 0 && SelectedConnectionIds.Count == 0)
            return Back("请先选择要下发的班级、分组或设备。");

        var group = (await extensionGroups.BuildForAllClassesAsync(ct: ct))
            .FirstOrDefault(x => string.Equals(x.Id, GroupId, StringComparison.Ordinal) && x.HasSettings);
        if (group is null) return Back("扩展插件不存在或尚未同步设置。");
        var values = ExtensionGroupService.ValidateForBatch(
            group, ExtensionFieldInput.ToValues(SettingInputs, onlyApplied: true), out var error);
        if (error is not null) return Back(error);
        if (values.Count == 0) return Back("请至少勾选一项要修改的设置。");

        var profile = await identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return RedirectToPage("/Login");
        var classNames = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var allDevices = await devices.ListAsync(ct);
        var results = new List<SettingsDispatchResult>();
        var targets = new List<DeviceInventory>();

        var explicitIds = SelectedConnectionIds.ToHashSet();
        targets.AddRange(allDevices.Where(x => x.ConnectionId is { } id && explicitIds.Contains(id)));

        if (SelectedClassIds.Count > 0 || SelectedGroupIds.Count > 0)
        {
            foreach (var classId in await classrooms.ResolveTargetClassIdsAsync(SelectedClassIds, SelectedGroupIds, ct))
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
        }

        var dispatched = await devices.DispatchAsync(
            targets.GroupBy(x => x.ConnectionId).Select(x => x.First()).ToList(),
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
            ? $"已下发 {ok} 项。"
            : $"下发完成 {ok} 项，失败 {results.Count - ok} 项，详见下方结果。";
        return RedirectToPage(new { groupId = GroupId });
    }

    /// <summary>系统管理员切换该插件是否允许各班班主任自行管理本班设置。</summary>
    public async Task<IActionResult> OnPostClassAdminAccessAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!ExtensionGroupPolicyService.IsValidGroupId(GroupId)) return RedirectToPage("/Denied");
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

    /// <summary>批量设置只对系统管理员开放；班主任与其他账号转到班级区的本班插件页。</summary>
    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin
            ? null
            : RedirectToPage("/ClassExtensions", new { groupId = GroupId });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Groups = (await extensionGroups.BuildForAllClassesAsync(ct: ct)).Where(x => x.HasSettings).ToList();
        ClassAdminAllowedGroupIds = await groupPolicies.ListClassAdminAllowedAsync(ct);
        if (string.IsNullOrWhiteSpace(GroupId)) return;

        Group = Groups.FirstOrDefault(x => string.Equals(x.Id, GroupId, StringComparison.Ordinal));
        if (Group is null) return;
        ClassStates = await extensionGroups.ListClassStatesAsync(GroupId, ct);
        ClassGroups = await classrooms.ListGroupsAsync(ct);
        var reportingClasses = ClassStates.Select(x => x.ClassId).ToHashSet();
        Devices = (await devices.ListAsync(ct))
            .Where(x => x.Assigned && reportingClasses.Contains(x.ClassId))
            .OrderByDescending(x => x.Online)
            .ThenBy(x => x.ClassName, StringComparer.CurrentCulture)
            .ThenBy(x => x.DeviceName, StringComparer.CurrentCulture)
            .ToList();
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

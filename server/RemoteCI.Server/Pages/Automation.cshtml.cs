using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>自动化规则列表与最近执行记录（仅系统管理员）。</summary>
[Authorize]
public sealed class AutomationModel(
    UserManager<AppUser> users,
    AutomationRuleService rules,
    AutomationEngine engine,
    ClassroomService classrooms) : WebPageModel(users)
{
    public IReadOnlyList<AutomationRuleDto> Rules { get; private set; } = [];
    public IReadOnlyList<AutomationRunView> Runs { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> ClassNames { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> GroupNames { get; private set; } = new Dictionary<Guid, string>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        Rules = await rules.ListAsync(ct);
        Runs = await rules.ListRunsAsync(null, 40, ct);
        ClassNames = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        GroupNames = (await classrooms.ListGroupsAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool enabled, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await rules.SetEnabledAsync(CurrentUser, id, enabled, ct);
            TempData["Message"] = enabled ? "规则已启用。" : "规则已停用，等待中的延迟任务已取消。";
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            var runs = await engine.RunNowAsync(CurrentUser, id, DateTimeOffset.UtcNow, ct);
            var failed = runs.Count(x => x.Status != nameof(AutomationRunStatus.Succeeded));
            if (runs.Count == 0) TempData["Error"] = "目标班级为空，没有执行。";
            else if (failed == 0) TempData["Message"] = $"已在 {runs.Count} 个班级执行完成。";
            else TempData["Error"] = $"执行 {runs.Count} 个班级，失败 {failed} 个，详见下方执行记录。";
        }
        catch (KeyNotFoundException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await rules.DeleteAsync(CurrentUser, id, ct);
            TempData["Message"] = "规则已删除。";
        }
        catch (KeyNotFoundException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public string DescribeTarget(AutomationTarget target)
    {
        var names = target.GroupIds.Select(x => GroupNames.TryGetValue(x, out var name) ? $"分组 {name}" : null)
            .Concat(target.ClassIds.Select(x => ClassNames.GetValueOrDefault(x)))
            .OfType<string>()
            .ToList();
        if (names.Count == 0) return "（目标已删除）";
        return names.Count <= 3 ? string.Join("、", names) : $"{string.Join("、", names.Take(3))} 等 {names.Count} 项";
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 档案版本历史与设备差异：查看每次保存改了什么、恢复历史版本，并检查教室电脑上的档案是否被本地修改过。
/// 系统管理员看全部档案；班主任只看自己可管理班级的档案。
/// </summary>
[Authorize]
public sealed class ProfileHistoryModel(
    UserManager<AppUser> users,
    ProfileLibraryService library,
    ProfileDriftService drift,
    ClassroomService classrooms) : WebPageModel(users)
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public long? Revision { get; set; }
    [BindProperty(SupportsGet = true)] public string? Against { get; set; }

    public IReadOnlyList<StoredProfileDto> Profiles { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> ClassNames { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, ProfileDriftView> Drift { get; private set; } = new Dictionary<Guid, ProfileDriftView>();
    public StoredProfileDto? Selected { get; private set; }
    public IReadOnlyList<ProfileVersionView> Versions { get; private set; } = [];
    public ProfileVersionDiff? Diff { get; private set; }
    public IReadOnlyList<ProfileDriftCheckResult> CheckResults { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Profiles = await library.ListManageableAsync(CurrentUser, ct);
        if (Profiles.Count == 0 && !HasManagement(RemoteCI.Shared.ManagementPermissions.ManageProfileLibrary)) return RedirectToPage("/Denied");
        ClassNames = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var classIds = Profiles.Where(x => x.ClassId is not null).Select(x => x.ClassId!.Value).ToList();
        Drift = await drift.LatestAsync(classIds, ct);
        if (TempData["DriftResults"] is string json)
            CheckResults = JsonSerializer.Deserialize<List<ProfileDriftCheckResult>>(json, JsonDefaults.Options) ?? [];

        if (Id is not { } id) return Page();
        Selected = Profiles.FirstOrDefault(x => x.Id == id);
        if (Selected is null) return NotFound();
        Versions = await library.ListVersionsAsync(CurrentUser, id, ct: ct);
        var revision = Revision ?? Versions.FirstOrDefault()?.Revision;
        if (revision is { } selectedRevision)
        {
            try
            {
                Diff = await library.DiffVersionAsync(CurrentUser, id, selectedRevision,
                    string.Equals(Against, "current", StringComparison.OrdinalIgnoreCase), ct: ct);
            }
            catch (KeyNotFoundException ex) { TempData["Error"] = ex.Message; }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(Guid id, long revision, long expectedRevision, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try
        {
            var saved = await library.RestoreVersionAsync(CurrentUser, id, revision, expectedRevision, ct: ct);
            TempData["Message"] = $"已恢复为第 {revision} 版，另存为第 {saved.Revision} 版。设备上的档案不会自动变化，需要时请重新下发。";
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or ProfileRevisionException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCheckDriftAsync(List<Guid> classIds, Guid? id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try
        {
            if (classIds.Count == 0) throw new ArgumentException("请选择要检查的班级。");
            var results = await drift.CheckAsync(CurrentUser, classIds, ct: ct);
            TempData["DriftResults"] = JsonSerializer.Serialize(results, JsonDefaults.Options);
            TempData["Message"] = $"已检查 {results.Count(x => x.Success)} 个班级，" +
                $"{results.Count(x => x.ChangeCount > 0)} 个与服务端档案不一致，{results.Count(x => !x.Success)} 个未能读取。";
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    public string ProfileTitle(StoredProfileDto profile) => profile.ClassId is { } classId
        ? $"{ClassNames.GetValueOrDefault(classId) ?? "未知班级"} · {profile.Name}"
        : $"全局模板 · {profile.Name}";
}

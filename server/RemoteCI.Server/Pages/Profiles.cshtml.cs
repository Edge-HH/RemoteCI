using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
[RequestSizeLimit(64 * 1024 * 1024)]
public class ProfilesModel(
    UserManager<AppUser> users,
    ProfileLibraryService library,
    ProfileDispatchService dispatch,
    DeviceInventoryService devices,
    ClassroomService classrooms) : WebPageModel(users)
{
    public virtual bool IsClassPage => false;
    public string InitialJson { get; private set; } = "{}";
    public string BootstrapJson => InitialJson;
    private Guid? OnlyClass => IsClassPage ? CurrentClassId : null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!await CanOpenAsync(ct)) return RedirectToPage("/Denied");
        InitialJson = JsonSerializer.Serialize(await DataAsync(ct), JsonDefaults.Options);
        return Page();
    }

    public Task<IActionResult> OnGetDataAsync(CancellationToken ct) => JsonActionAsync(async () =>
        await DataAsync(ct), ct);

    public Task<IActionResult> OnPostPreviewAsync([FromBody] ProfilePreviewRequest input, CancellationToken ct) => JsonActionAsync(() =>
    {
        var root = ProfileDocument.Parse(input.ProfileJson);
        var json = ProfileDocument.Serialize(root);
        return Task.FromResult<object>(new { success = true, preview = ProfileLibraryService.Preview(json), profileJson = json });
    }, ct);

    public Task<IActionResult> OnPostSaveAsync([FromBody] ProfileSaveRequest input, CancellationToken ct) => JsonActionAsync(async () =>
        new { success = true, profiles = await library.SaveAsync(CurrentUser, input.Items, OnlyClass, ct), message = "档案已保存，尚未下发到设备。" }, ct);

    public Task<IActionResult> OnPostCopyAsync([FromBody] ProfileIdRequest input, CancellationToken ct) => JsonActionAsync(async () =>
    {
        if (IsClassPage) throw new UnauthorizedAccessException("本班页面不能复制全局模板。");
        return new { success = true, profile = await library.CopyAsync(CurrentUser, input, ct), message = "模板副本已创建。" };
    }, ct);

    public Task<IActionResult> OnPostDeleteAsync([FromBody] ProfileIdRequest input, CancellationToken ct) => JsonActionAsync(async () =>
    {
        await library.DeleteAsync(CurrentUser, input, OnlyClass, ct);
        return new { success = true, message = "服务端档案已删除，设备档案不受影响。" };
    }, ct);

    public Task<IActionResult> OnPostApplyAsync([FromBody] ProfileDispatchRequest input, CancellationToken ct) => JsonActionAsync(async () =>
    {
        var results = await dispatch.ApplyAsync(CurrentUser, input, OnlyClass, ct);
        return new { success = results.Any(x => x.Success), results,
            message = $"完成 {results.Count(x => x.Success)} 台，失败 {results.Count(x => !x.Success)} 台。" };
    }, ct);

    public Task<IActionResult> OnPostCollectAsync([FromBody] ProfileCollectRequest input, CancellationToken ct) => JsonActionAsync(async () =>
    {
        var results = await dispatch.CollectAsync(CurrentUser, input, OnlyClass, ct);
        return new { success = results.Any(x => x.Success), results, message = ProfileCollectResult.Summary(results) };
    }, ct);

    public async Task<IActionResult> OnGetExportAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!await CanOpenAsync(ct)) return StatusCode(403);
        try
        {
            var profile = await library.GetAsync(CurrentUser, id, OnlyClass, ct);
            var safeName = string.Concat(profile.Name.Select(x => "<>:\"/\\|?*".Contains(x) || char.IsControl(x) ? '_' : x));
            return File(Encoding.UTF8.GetBytes(profile.ProfileJson), "application/json", $"{safeName}.json");
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private async Task<bool> CanOpenAsync(CancellationToken ct) => IsClassPage
        ? CurrentClass is not null && await library.CanManageClassAsync(CurrentUser, CurrentClassId, ct)
        : CurrentUser.Role == UserRole.Admin;

    private async Task<object> DataAsync(CancellationToken ct)
    {
        var profiles = await library.ListAsync(CurrentUser, OnlyClass, ct);
        var allDevices = await devices.ListAsync(ct);
        var classes = IsClassPage ? AccessibleClasses.Where(x => x.Id == CurrentClassId) : AccessibleClasses;
        IReadOnlyList<ClassGroupInfo> groups = IsClassPage ? [] : await classrooms.ListGroupsAsync(ct);
        return new
        {
            isAdmin = CurrentUser.Role == UserRole.Admin,
            currentClassId = CurrentClassId,
            profiles,
            classes = classes.Select(x => new { x.Id, x.Name }),
            groups = groups.Select(x => new { x.Id, x.Name }),
            devices = allDevices.Where(x => !IsClassPage || x.ClassId == CurrentClassId)
                .Select(x => new { x.ConnectionId, x.CredentialId, x.ClassId, x.DeviceName, x.Online, x.Capabilities }),
        };
    }

    private async Task<IActionResult> JsonActionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!await CanOpenAsync(ct)) return Error("没有管理此档案的权限。", 403);
        if (!ModelState.IsValid) return Error("请求内容格式无效。", 400);
        try { return new JsonResult(await action()); }
        catch (UnauthorizedAccessException ex) { return Error(ex.Message, 403); }
        catch (KeyNotFoundException ex) { return Error(ex.Message, 404); }
        catch (ProfileRevisionException ex) { return Error(ex.Message, 409); }
        catch (ArgumentException ex) { return Error(ex.Message, 400); }
        catch (JsonException) { return Error("档案 JSON 格式无效。", 400); }
    }
    private static JsonResult Error(string message, int status) => new(new { success = false, message }) { StatusCode = status };
}

public sealed class ProfilePreviewRequest { public string ProfileJson { get; set; } = string.Empty; }
public sealed class ProfileSaveRequest { public List<ProfileSaveItem> Items { get; set; } = []; }

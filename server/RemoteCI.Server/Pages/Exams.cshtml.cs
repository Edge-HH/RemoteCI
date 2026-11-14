using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>管理中的考试：选定班级后经教室插件安装 ExamAware，并分发考试档案。</summary>
[Authorize]
[RequestSizeLimit(ExamDispatchService.MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = ExamDispatchService.MaxUploadBytes)]
public sealed class ExamsModel(
    UserManager<AppUser> users,
    ClassroomService classrooms,
    DeviceInventoryService devices,
    ExamRegistry exams,
    ExamDispatchService dispatch,
    ExamReleaseService releases,
    IdentityCoordinator identities) : WebPageModel(users)
{
    public IReadOnlyList<ClassRow> Classes { get; private set; } = [];
    public IReadOnlyList<JobListItem> Jobs { get; private set; } = [];
    public string? InstallerName { get; private set; }
    public string? PluginName { get; private set; }
    public string? InstallerSource { get; private set; }
    public string? PluginSource { get; private set; }
    public string? ExamName { get; private set; }
    public ExamReleasePreview? Releases { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public List<Guid> ClassIds { get; set; } = [];
    [BindProperty] public bool InstallPlugin { get; set; }
    [BindProperty] public bool LaunchAfter { get; set; }
    [BindProperty] public bool UseUploadedPackages { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await GuardAsync() is { } denied) return denied;
        Notice = TempData["ExamNotice"] as string;
        Error = TempData["ExamError"] as string;
        InstallPlugin = true;
        LaunchAfter = true;
        await LoadAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostUploadInstallerAsync(IFormFile? file, CancellationToken ct) =>
        UploadAsync(file, "installer", ct);

    public Task<IActionResult> OnPostUploadPluginAsync(IFormFile? file, CancellationToken ct) =>
        UploadAsync(file, "plugin", ct);

    public Task<IActionResult> OnPostUploadExamAsync(IFormFile? file, CancellationToken ct) =>
        UploadAsync(file, "exam", ct);

    public async Task<IActionResult> OnPostInstallAsync(CancellationToken ct)
    {
        if (await GuardAsync() is { } denied) return denied;
        if (!Permissions.HasFlag(UserPermissions.UpgradeSoftware))
            return Reject("安装 ExamAware 还需要“升级软件”权限。");
        if (ClassIds.Count == 0) return Reject("请选择至少一个班级。");
        var profile = await ProfileAsync(ct);
        return await RunAsync(
            () => dispatch.InstallAsync(ClassIds, InstallPlugin, LaunchAfter, UseUploadedPackages, profile, ct), ct);
    }

    public async Task<IActionResult> OnPostDistributeAsync(CancellationToken ct)
    {
        if (await GuardAsync() is { } denied) return denied;
        if (!Permissions.HasFlag(UserPermissions.SendFiles))
            return Reject("分发考试还需要“文件分发”权限。");
        if (ClassIds.Count == 0) return Reject("请选择至少一个班级。");
        var profile = await ProfileAsync(ct);
        return await RunAsync(() => dispatch.DistributeAsync(ClassIds, profile, ct), ct);
    }

    private async Task<IActionResult> UploadAsync(IFormFile? file, string kind, CancellationToken ct)
    {
        if (await GuardAsync() is { } denied) return denied;
        AllowLargeBody();
        if (file is null || file.Length == 0) return Reject("请选择要上传的文件。");
        if (file.Length > ExamDispatchService.MaxUploadBytes) return Reject("文件不能超过 512 MB。");
        try
        {
            await using var stream = file.OpenReadStream();
            var name = await dispatch.StageAsync(stream, file.FileName, kind, await ProfileAsync(ct), ct);
            TempData["ExamNotice"] = $"已上传 {name}。";
        }
        catch (FileTransferException ex)
        {
            TempData["ExamError"] = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task<IActionResult> RunAsync(
        Func<Task<IReadOnlyList<DeviceCommandResult>>> action, CancellationToken ct)
    {
        try
        {
            var results = await action();
            TempData[results.All(x => x.Success) ? "ExamNotice" : "ExamError"] = Format(results);
        }
        catch (FileTransferException ex)
        {
            TempData["ExamError"] = ex.Message;
        }
        catch (HttpRequestException)
        {
            TempData["ExamError"] = "连接 GitHub 失败，请稍后再安装。";
        }
        return RedirectToPage();
    }

    private IActionResult Reject(string message)
    {
        TempData["ExamError"] = message;
        return RedirectToPage();
    }

    private async Task<IActionResult?> GuardAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return HasManagement(ManagementPermissions.ManageExams) ? null : RedirectToPage("/Denied");
    }

    private async Task<UserProfile> ProfileAsync(CancellationToken ct) =>
        await identities.GetProfileAsync(CurrentUser.Id, ct)
        ?? throw new InvalidOperationException("当前账号不存在");

    private async Task LoadAsync(CancellationToken ct)
    {
        var online = (await devices.ListAsync(ct))
            .Where(x => x.Assigned && x.Online)
            .Select(x => x.ClassId)
            .ToHashSet();
        var agents = exams.ListAgents().ToDictionary(x => x.ClassId);
        var now = DateTimeOffset.UtcNow;
        Classes = (await classrooms.ListAsync(ct)).Select(classroom =>
        {
            agents.TryGetValue(classroom.Id, out var agent);
            var bound = agent is not null && now - agent.LastSeenAt <= ExamRegistry.AgentOnlineWindow;
            return new ClassRow(classroom.Id, classroom.Name, online.Contains(classroom.Id), bound,
                agent?.LastSeenAt);
        }).ToList();
        Jobs = exams.RecentJobs();
        var installer = exams.Staged("installer");
        var plugin = exams.Staged("plugin");
        InstallerName = installer?.FileName;
        PluginName = plugin?.FileName;
        InstallerSource = installer?.SourceKey;
        PluginSource = plugin?.SourceKey;
        ExamName = exams.Staged("exam")?.FileName;
        Releases = await releases.PreviewAsync(ct);
    }

    private void AllowLargeBody()
    {
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ExamDispatchService.MaxUploadBytes;
    }

    private static string Format(IReadOnlyList<DeviceCommandResult> results)
    {
        if (results.Count == 0) return "没有可下发的班级。";
        var text = new StringBuilder();
        foreach (var result in results)
            text.Append(result.DeviceName).Append(result.Success ? "：" : "失败：").Append(result.Message).Append(' ');
        return text.ToString().Trim();
    }

    public sealed record ClassRow(Guid Id, string Name, bool PluginOnline, bool ExamBound, DateTimeOffset? ExamSeenAt);
}

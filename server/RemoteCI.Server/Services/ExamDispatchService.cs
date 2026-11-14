using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>考试页把已上传的安装包和考试档案经文件中转下发到选定班级的教室插件。</summary>
public sealed class ExamDispatchService(
    FileTransferService transfers,
    DeviceInventoryService devices,
    ExamRegistry registry,
    ExamReleaseService releases)
{
    public const long MaxUploadBytes = 512L * 1024 * 1024;

    public async Task<string> StageAsync(Stream body, string? fileName, string kind, UserProfile owner, CancellationToken ct)
    {
        var safeName = FileDistributionRequest.SanitizeFileName(fileName)
            ?? throw new FileTransferException(ApiErrorCodes.InvalidRequest, "文件名无效");
        if (!ExtensionMatches(kind, safeName))
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, ExtensionHint(kind));
        var record = await transfers.CreateAsync(body, safeName, owner, ct);
        registry.Stage(kind, record.Id, record.FileName, record.Size);
        return record.FileName;
    }

    public async Task<IReadOnlyList<DeviceCommandResult>> InstallAsync(
        IReadOnlyCollection<Guid> classIds,
        bool installPlugin,
        bool launchAfter,
        bool useUploadedPackages,
        UserProfile requestedBy,
        CancellationToken ct)
    {
        if (!useUploadedPackages)
            await releases.PrepareAsync(requestedBy, installPlugin, ct);
        var installer = RequireStaged("installer", useUploadedPackages
            ? "请先上传 ExamAware 安装程序（.exe）"
            : "没有可用的 ExamAware 安装包");
        ExamFileRef? plugin = null;
        if (installPlugin)
            plugin = RequireStaged("plugin", useUploadedPackages
                ? "同时安装插件时，请先上传 ExamAware 插件包（.ea2x）"
                : "没有可用的考试插件包");
        return await SendAsync(classIds, "安装 ExamAware", target =>
        {
            Register(installer, target);
            if (plugin is not null) Register(plugin, target);
            return new CommandMessage
            {
                Command = CommandKind.InstallExamAware,
                RequestedBy = requestedBy,
                ClassId = target.ClassId,
                ExamInstall = new ExamInstallRequest
                {
                    Installer = installer,
                    Plugin = plugin,
                    InstallPlugin = installPlugin,
                    LaunchAfter = launchAfter,
                },
            };
        }, ct);
    }

    public async Task<IReadOnlyList<DeviceCommandResult>> DistributeAsync(
        IReadOnlyCollection<Guid> classIds, UserProfile requestedBy, CancellationToken ct)
    {
        var exam = RequireStaged("exam", "请先上传考试档案（.ea2）");
        var plan = await devices.ResolveAsync(classIds, [], [], ct);
        var jobs = plan.Targets.ToDictionary(
            target => target.ConnectionId!.Value,
            target => registry.CreateJob(target.ClassId, target.ClassName, exam.FileName, exam.Sha256));
        var results = await SendResolvedAsync(plan, "分发考试", target =>
        {
            Register(exam, target);
            var job = jobs[target.ConnectionId!.Value];
            return new CommandMessage
            {
                Command = CommandKind.DistributeExam,
                RequestedBy = requestedBy,
                ClassId = target.ClassId,
                ExamDistribute = new ExamDistributeRequest
                {
                    JobId = job.Id,
                    LocalFileName = job.LocalFileName,
                    Exam = exam,
                },
            };
        }, ct);
        foreach (var result in results.Where(x => !x.Success))
        {
            var target = plan.Targets.FirstOrDefault(x => x.ConnectionId == result.ConnectionId);
            if (target?.ConnectionId is { } id && jobs.TryGetValue(id, out var job))
                registry.FailJob(job.Id, result.Message);
        }
        foreach (var failure in plan.Failures)
            results.Add(failure);
        return results;
    }

    private async Task<List<DeviceCommandResult>> SendAsync(
        IReadOnlyCollection<Guid> classIds,
        string actionName,
        Func<DeviceInventory, CommandMessage> commandFactory,
        CancellationToken ct)
    {
        var plan = await devices.ResolveAsync(classIds, [], [], ct);
        var results = await SendResolvedAsync(plan, actionName, commandFactory, ct);
        results.AddRange(plan.Failures);
        return results;
    }

    private async Task<List<DeviceCommandResult>> SendResolvedAsync(
        DeviceDispatchPlan plan,
        string actionName,
        Func<DeviceInventory, CommandMessage> commandFactory,
        CancellationToken ct)
    {
        var sent = await devices.DispatchAsync(
            plan.Targets, commandFactory, actionName, TimeSpan.FromSeconds(20), ct);
        return sent.ToList();
    }

    private void Register(ExamFileRef file, DeviceInventory target)
    {
        var record = transfers.Get(file.TransferId)
            ?? throw new FileTransferException(ApiErrorCodes.NotFound, "文件已过期，请重新上传");
        transfers.AddDelivery(record, new FileTransferDelivery
        {
            CredentialId = target.CredentialId,
            ClassId = target.ClassId,
            TargetName = target.ClassName,
            State = FileTransferState.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private ExamFileRef RequireStaged(string kind, string missing)
    {
        var staged = registry.Staged(kind) ?? throw new FileTransferException(ApiErrorCodes.InvalidRequest, missing);
        var record = transfers.Get(staged.TransferId)
            ?? throw new FileTransferException(ApiErrorCodes.InvalidRequest, "文件已过期，请重新上传");
        return new ExamFileRef
        {
            FileName = record.FileName,
            TransferId = record.Id,
            DownloadKey = record.DownloadKey,
            Size = record.Size,
            Sha256 = record.Sha256,
        };
    }

    private static bool ExtensionMatches(string kind, string fileName) => kind switch
    {
        "installer" => fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        "plugin" => fileName.EndsWith(".ea2x", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
        "exam" => fileName.EndsWith(".ea2", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string ExtensionHint(string kind) => kind switch
    {
        "installer" => "安装程序必须是 .exe",
        "plugin" => "插件包必须是 .ea2x 或 .zip",
        "exam" => "考试档案必须是 .ea2",
        _ => "文件类型无效",
    };
}

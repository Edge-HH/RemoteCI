using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// ClassIsland 档案分发与时间表修改。所有写入都在 UI 线程执行，并复用宿主的档案保存机制。
/// </summary>
public sealed class ProfileManagementService(ILogger<ProfileManagementService> logger)
{
    private const int MaxProfileJsonLength = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public async Task<CommandResult> UpdateTimeLayoutAsync(TimeLayoutUpdateRequest? request, CancellationToken ct = default)
    {
        if (request is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少时间表参数");
        if (string.IsNullOrWhiteSpace(request.Name))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "时间表名称不能为空");
        if (request.Points.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "时间表至少需要一个时间点");
        if (request.Points.Count > 64)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "单个时间表最多支持 64 个时间点");

        var points = new List<TimeLayoutItem>(request.Points.Count);
        foreach (var point in request.Points)
        {
            if (!TimeSpan.TryParse(point.StartTime, out var start) ||
                !TimeSpan.TryParse(point.EndTime, out var end))
                return CommandResult.Failure(CommandResultCodes.InvalidRequest, "时间点必须使用 HH:mm 格式");
            if (end < start)
                return CommandResult.Failure(CommandResultCodes.InvalidRequest, "时间点结束时间不能早于开始时间");
            if (point.TimeType is < 0 or > 3)
                return CommandResult.Failure(CommandResultCodes.InvalidRequest, "时间点类型必须是 0、1、2 或 3");

            points.Add(new TimeLayoutItem
            {
                StartTime = start,
                EndTime = end,
                TimeType = point.TimeType,
                BreakName = point.BreakName?.Trim() ?? string.Empty,
            });
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var profileService = IAppHost.GetService<IProfileService>();
                var profile = profileService.Profile;
                var id = request.TimeLayoutId ?? Guid.NewGuid();
                if (request.Activate)
                {
                    foreach (var existing in profile.TimeLayouts.Values)
                        existing.IsActivated = false;
                }

                var layout = new TimeLayout
                {
                    Name = request.Name.Trim(),
                    IsActivated = request.Activate,
                    IsActivatedManually = request.Activate,
                };
                foreach (var point in points.OrderBy(point => point.StartTime).ThenBy(point => point.EndTime))
                    layout.Layouts.Add(point);

                profile.TimeLayouts[id] = layout;
                profileService.SaveProfile();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改 ClassIsland 时间表失败");
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"修改时间表失败：{ex.Message}");
        }

        if (request.RestartAfter)
            ScheduleRestart();
        return Success(request.RestartAfter ? "时间表已更新，ClassIsland 将自动重启" : "时间表已更新");
    }

    public async Task<CommandResult> DistributeProfileAsync(
        ProfileDistributionRequest? request,
        CancellationToken ct = default)
    {
        if (request is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少档案分发参数");
        if (request.Sections == ProfileDistributionSection.None)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "请至少选择一个分发内容");
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "档案 JSON 不能为空");
        if (request.ProfileJson.Length > MaxProfileJsonLength)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "档案 JSON 不能超过 5 MB");

        Profile source;
        try
        {
            source = JsonSerializer.Deserialize<Profile>(request.ProfileJson, ProfileJsonOptions)
                ?? throw new InvalidOperationException("档案 JSON 为空");
        }
        catch (Exception ex)
        {
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"档案 JSON 无效：{ex.Message}");
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var profileService = IAppHost.GetService<IProfileService>();
                var target = profileService.Profile;
                if (request.Sections.HasFlag(ProfileDistributionSection.TimeLayouts))
                    ApplyDictionary(target.TimeLayouts, source.TimeLayouts, request.ReplaceExisting);
                if (request.Sections.HasFlag(ProfileDistributionSection.ClassPlans))
                    ApplyDictionary(target.ClassPlans, source.ClassPlans, request.ReplaceExisting);
                if (request.Sections.HasFlag(ProfileDistributionSection.Subjects))
                    ApplyDictionary(target.Subjects, source.Subjects, request.ReplaceExisting);
                profileService.SaveProfile();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "分发 ClassIsland 档案失败");
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"分发档案失败：{ex.Message}");
        }

        if (request.RestartAfter)
            ScheduleRestart();
        var sections = DescribeSections(request.Sections);
        return Success($"已分发{sections}{(request.RestartAfter ? "，ClassIsland 将自动重启" : string.Empty)}");
    }

    private static void ApplyDictionary<T>(
        IDictionary<Guid, T> target,
        IDictionary<Guid, T> source,
        bool replaceExisting)
    {
        if (replaceExisting)
            target.Clear();
        foreach (var (id, value) in source)
            target[id] = value;
    }

    private static string DescribeSections(ProfileDistributionSection sections)
    {
        var names = new List<string>();
        if (sections.HasFlag(ProfileDistributionSection.TimeLayouts)) names.Add("时间表");
        if (sections.HasFlag(ProfileDistributionSection.ClassPlans)) names.Add("课表");
        if (sections.HasFlag(ProfileDistributionSection.Subjects)) names.Add("科目");
        return string.Join("、", names);
    }

    private static void ScheduleRestart()
    {
        // 档案已通过宿主 PropertyChanged 自动保存；延迟重启是为了让命令回执先发出。
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Dispatcher.UIThread.Post(() => AppBase.Current.Restart());
        });
    }

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}



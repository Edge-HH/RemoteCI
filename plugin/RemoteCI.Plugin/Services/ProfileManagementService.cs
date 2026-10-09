using System.Text.Json;
using System.Reflection;
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

    /// <summary>新档案页采用候选验证与快照回滚；旧命令保持原协议行为。</summary>
    public async Task<CommandResult> ApplyProfileAsync(ProfileApplyRequest? request, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return CommandResult.Failure(CommandResultCodes.InvalidRequest, "档案应用已取消");
        var result = await Dispatcher.UIThread.InvokeAsync(() => ProfileApplyExecutor.Apply(request,
            new ClassIslandProfileApplicationBackend(IAppHost.GetService<IProfileService>()),
            error => logger.LogError(error, "应用服务端档案失败")));
        if (result.Success && request?.RestartAfter == true)
        {
            ScheduleRestart();
            result.Message += "，ClassIsland 将自动重启";
        }
        return result;
    }

    /// <summary>读取宿主当前内存档案，供服务端档案管理收集；不写入任何内容。</summary>
    public async Task<CommandResult> ReadProfileAsync() => await Dispatcher.UIThread.InvokeAsync(() => ProfileApplyExecutor.Read(
        new ClassIslandProfileApplicationBackend(IAppHost.GetService<IProfileService>()),
        error => logger.LogError(error, "读取 ClassIsland 档案失败")));

    public async Task<CommandResult> UpdateTimeLayoutAsync(TimeLayoutUpdateRequest? request, CancellationToken ct = default)
    {
        if (ValidateTimeLayoutRequest(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        var validRequest = request!;
        if (!TryBuildTimeLayoutItems(validRequest, out var points, out error))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error!);

        try
        {
            await SaveTimeLayoutAsync(validRequest, points);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改 ClassIsland 时间表失败");
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"修改时间表失败：{ex.Message}");
        }

        if (validRequest.RestartAfter)
            ScheduleRestart();
        return Success(validRequest.RestartAfter ? "时间表已更新，ClassIsland 将自动重启" : "时间表已更新");
    }

    private static string? ValidateTimeLayoutRequest(TimeLayoutUpdateRequest? request)
    {
        if (request is null) return "缺少时间表参数";
        if (string.IsNullOrWhiteSpace(request.Name)) return "时间表名称不能为空";
        if (request.Points.Count == 0) return "时间表至少需要一个时间点";
        return request.Points.Count > 64 ? "单个时间表最多支持 64 个时间点" : null;
    }

    private static bool TryBuildTimeLayoutItems(
        TimeLayoutUpdateRequest request,
        out List<TimeLayoutItem> points,
        out string? error)
    {
        points = new List<TimeLayoutItem>(request.Points.Count);
        foreach (var point in request.Points)
        {
            if (!TimeSpan.TryParse(point.StartTime, out var start) ||
                !TimeSpan.TryParse(point.EndTime, out var end))
            {
                error = "时间点必须使用 HH:mm 格式";
                return false;
            }
            if (end < start)
            {
                error = "时间点结束时间不能早于开始时间";
                return false;
            }
            if (point.TimeType is < 0 or > 3)
            {
                error = "时间点类型必须是 0、1、2 或 3";
                return false;
            }
            points.Add(new TimeLayoutItem
            {
                StartTime = start,
                EndTime = end,
                TimeType = point.TimeType,
                BreakName = point.BreakName?.Trim() ?? string.Empty,
            });
        }
        error = null;
        return true;
    }

    private static async Task SaveTimeLayoutAsync(TimeLayoutUpdateRequest request, IReadOnlyList<TimeLayoutItem> points)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var profileService = IAppHost.GetService<IProfileService>();
            var profile = profileService.Profile;
            // ClassIsland 2.2 更换了 Profile 字典的具体返回类型。
            // 不能在这里直接绑定旧版 getter，否则新宿主会抛 MissingMethodException。
            var timeLayouts = HostApiCompat.ReadProperty<IDictionary<Guid, TimeLayout>>(profile, "TimeLayouts");
            var id = request.TimeLayoutId ?? Guid.NewGuid();
            if (request.Activate)
                foreach (var existing in timeLayouts.Values)
                    existing.IsActivated = false;

            var layout = new TimeLayout
            {
                Name = request.Name.Trim(),
                IsActivated = request.Activate,
                IsActivatedManually = request.Activate,
            };
            foreach (var point in points.OrderBy(point => point.StartTime).ThenBy(point => point.EndTime))
                layout.Layouts.Add(point);
            timeLayouts[id] = layout;
            profileService.SaveProfile();
        });
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
                // 通过反射读取属性，兼容旧版 ObservableDictionary 与新版实现。
                var targetTimeLayouts = HostApiCompat.ReadProperty<IDictionary<Guid, TimeLayout>>(target, "TimeLayouts");
                var targetClassPlans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(target, "ClassPlans");
                var targetSubjects = HostApiCompat.ReadProperty<IDictionary<Guid, Subject>>(target, "Subjects");
                var sourceTimeLayouts = HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, TimeLayout>>(source, "TimeLayouts");
                var sourceClassPlans = HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, ClassPlan>>(source, "ClassPlans");
                var sourceSubjects = HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(source, "Subjects");
                if (request.ReplaceCurrentProfile)
                {
                    ApplyImportedSections(target, targetTimeLayouts, targetClassPlans, targetSubjects,
                        sourceTimeLayouts, sourceClassPlans, sourceSubjects, request);
                    profileService.SaveProfile();
                }
                else
                {
                    var importName = NormalizeImportName(request.ImportProfileName, source.Name);
                    var profileType = profileService.GetType();
                    var profilePath = profileType.GetProperty("ProfilePath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(profilePath))
                        throw new InvalidOperationException("宿主未提供档案目录");
                    var filename = $"{importName}.json";
                    var destination = Path.Combine(profilePath, filename);
                    if (File.Exists(destination))
                        throw new InvalidOperationException($"档案“{importName}”已存在，请更换导入档案名");

                    var imported = new Profile { Name = importName };
                    ApplyImportedSections(imported,
                        HostApiCompat.ReadProperty<IDictionary<Guid, TimeLayout>>(imported, "TimeLayouts"),
                        HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(imported, "ClassPlans"),
                        HostApiCompat.ReadProperty<IDictionary<Guid, Subject>>(imported, "Subjects"),
                        sourceTimeLayouts, sourceClassPlans, sourceSubjects, request);

                    var save = profileType.GetMethod("SaveProfile", [typeof(string)]);
                    if (save is null)
                        throw new InvalidOperationException("宿主未提供档案保存接口");
                    // SaveProfile(string) serializes the service's current Profile, so temporarily
                    // swap it even when the imported file should remain disabled.
                    profileType.GetProperty("Profile")?.SetValue(profileService, imported);
                    if (request.EnableImportedProfile)
                    {
                        profileType.GetProperty("CurrentProfilePath")?.SetValue(profileService, filename);
                        SetSelectedProfile(profileService, filename);
                    }
                    save.Invoke(profileService, [filename]);
                    if (!request.EnableImportedProfile)
                        profileType.GetProperty("Profile")?.SetValue(profileService, target);
                }
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
        var profileHint = request.ReplaceCurrentProfile ? "当前档案" : $"档案“{NormalizeImportName(request.ImportProfileName, source.Name)}”";
        return Success($"已导入{profileHint}的{sections}{(request.RestartAfter ? "，ClassIsland 将自动重启" : string.Empty)}");
    }

    private static void ApplyImportedSections(
        Profile target,
        IDictionary<Guid, TimeLayout> targetTimeLayouts,
        IDictionary<Guid, ClassPlan> targetClassPlans,
        IDictionary<Guid, Subject> targetSubjects,
        IReadOnlyDictionary<Guid, TimeLayout> sourceTimeLayouts,
        IReadOnlyDictionary<Guid, ClassPlan> sourceClassPlans,
        IReadOnlyDictionary<Guid, Subject> sourceSubjects,
        ProfileDistributionRequest request)
    {
        if (request.Sections.HasFlag(ProfileDistributionSection.TimeLayouts))
            ApplyDictionary(targetTimeLayouts, sourceTimeLayouts, request.ReplaceExisting);
        if (request.Sections.HasFlag(ProfileDistributionSection.ClassPlans))
            ApplyDictionary(targetClassPlans, sourceClassPlans, request.ReplaceExisting);
        if (request.Sections.HasFlag(ProfileDistributionSection.Subjects))
            ApplyDictionary(targetSubjects, sourceSubjects, request.ReplaceExisting);
    }

    private static string NormalizeImportName(string? requested, string? sourceName)
    {
        var raw = (string.IsNullOrWhiteSpace(requested) ? sourceName : requested)?.Trim();
        if (!string.IsNullOrWhiteSpace(raw) && (raw.Contains(Path.DirectorySeparatorChar) || raw.Contains(Path.AltDirectorySeparatorChar)))
            throw new InvalidOperationException("导入档案名不能包含目录路径");
        var name = Path.GetFileNameWithoutExtension(raw)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "RemoteCI 导入档案";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new InvalidOperationException("导入档案名包含非法字符");
        return name;
    }

    private static void SetSelectedProfile(object profileService, string filename)
    {
        var settingsService = profileService.GetType().GetProperty("SettingsService",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(profileService);
        var settings = settingsService?.GetType().GetProperty("Settings")?.GetValue(settingsService);
        var selected = settings?.GetType().GetProperty("SelectedProfile");
        selected?.SetValue(settings, filename);
        settingsService?.GetType().GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(settingsService, null);
    }

    private static void ApplyDictionary<T>(
        IDictionary<Guid, T> target,
        IReadOnlyDictionary<Guid, T> source,
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



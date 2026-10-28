using System.Reflection;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 安装服务端中转的 .cipx 插件包（服务端预下载与上传 CIPX 共用）。
/// 与 ClassIsland“从本地安装插件”相同：把插件包放进宿主插件包缓存（PluginPackages），重启后由宿主解压安装；
/// RemoteCI 不直接改写 Plugins 目录。命令立即回执，下载、校验与重启在后台完成，结果随软件清单的最近升级状态回传。
/// </summary>
public sealed class PluginPackageInstallService
{
    private readonly FileReceiveService _files;
    private readonly SoftwareInventoryService _inventory;
    private readonly PluginManagementService _management;
    private readonly ILogger<PluginPackageInstallService> _logger;

    public PluginPackageInstallService(
        FileReceiveService files,
        SoftwareInventoryService inventory,
        PluginManagementService management,
        ILogger<PluginPackageInstallService> logger)
    {
        _files = files;
        _inventory = inventory;
        _management = management;
        _logger = logger;
    }

    public CommandResult Start(PluginPackageInstallRequest? request)
    {
        if (Validate(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        if (request!.Mode == PluginPackageInstallMode.Install && !_management.LoadPolicy().AllowRemoteInstall)
            return CommandResult.Failure(CommandResultCodes.Forbidden, "设备策略禁止远程安装插件");
        if (_files.Channel is null)
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未连接云端，无法下载插件包");

        var plan = Plan(request, InstalledVersions());
        if (plan.Apply.Count == 0)
        {
            // 没有需要安装的包：逐个回报“已跳过”，服务端的接收进度也随之结束。
            foreach (var skipped in plan.Skipped)
                _ = _files.ReportFinalAsync(skipped.Package.ToTransferRequest(), true, skipped.Reason);
            return Success(request.Mode == PluginPackageInstallMode.Upgrade
                ? "插件均已是最新版本或未安装，无需更新"
                : "请求的插件均已安装");
        }

        if (!_inventory.TryBeginOperation())
            return CommandResult.Failure(CommandResultCodes.Busy, "已有软件升级任务正在执行");
        _ = Task.Run(() => RunAsync(plan, request.RestartAfter));
        return Success($"开始接收 {plan.Apply.Count} 个插件包，完成后{(request.RestartAfter ? " ClassIsland 将自动重启" : "重启 ClassIsland 生效")}");
    }

    private async Task RunAsync(PackagePlan plan, bool restartAfter)
    {
        try
        {
            foreach (var skipped in plan.Skipped)
                await _files.ReportFinalAsync(skipped.Package.ToTransferRequest(), true, skipped.Reason);

            var packageRoot = ResolvePackageRoot();
            Directory.CreateDirectory(packageRoot);
            var installed = 0;
            var failures = new List<string>();
            foreach (var package in plan.Apply)
            {
                _inventory.ReportPluginStatus(SoftwareUpdateState.Running,
                    $"正在接收插件包 {DisplayName(package)}（{installed + failures.Count + 1}/{plan.Apply.Count}）");
                var transfer = package.ToTransferRequest();
                var staged = await _files.ReceiveToStagingAsync(transfer, CancellationToken.None);
                if (staged is null)
                {
                    failures.Add($"{DisplayName(package)}：下载失败");
                    continue;
                }

                try
                {
                    var manifestId = PluginPackageManifest.TryRead(staged)?.Id;
                    if (!string.Equals(manifestId, package.PluginId, StringComparison.OrdinalIgnoreCase))
                    {
                        var message = manifestId is null
                            ? "插件包缺少 manifest.yml，不是有效的 ClassIsland 插件包"
                            : $"插件包 Id（{manifestId}）与请求（{package.PluginId}）不一致";
                        failures.Add($"{DisplayName(package)}：{message}");
                        await _files.ReportFinalAsync(transfer, false, message);
                        continue;
                    }

                    File.Move(staged, Path.Combine(packageRoot, package.PluginId + IPluginService.PluginPackageExtension), true);
                    installed++;
                    await _files.ReportFinalAsync(transfer, true, $"已放入插件包缓存（{package.Version}），重启后安装");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _logger.LogError(ex, "写入插件包失败：{Id}", package.PluginId);
                    failures.Add($"{DisplayName(package)}：{ex.Message}");
                    await _files.ReportFinalAsync(transfer, false, $"写入插件包失败：{ex.Message}");
                }
                finally
                {
                    TryDelete(staged);
                }
            }

            if (installed == 0)
            {
                _inventory.ReportPluginStatus(SoftwareUpdateState.Failed, $"插件包安装失败：{string.Join("；", failures)}");
                return;
            }

            var summary = failures.Count == 0
                ? $"已接收 {installed} 个插件包"
                : $"已接收 {installed} 个插件包，失败 {failures.Count} 个：{string.Join("；", failures)}";
            _inventory.ReportPluginStatus(SoftwareUpdateState.Completed,
                restartAfter ? $"{summary}，ClassIsland 即将重启" : $"{summary}，重启 ClassIsland 后生效");
            if (!restartAfter) return;
            await Task.Delay(750);
            Dispatcher.UIThread.Post(() => AppBase.Current.Restart());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装服务端插件包失败");
            _inventory.ReportPluginStatus(SoftwareUpdateState.Failed, $"插件包安装失败：{ex.Message}");
        }
        finally
        {
            _inventory.EndOperation();
        }
    }

    internal static string? Validate(PluginPackageInstallRequest? request)
    {
        if (request is null || request.Packages.Count == 0) return "缺少插件包";
        if (!Enum.IsDefined(request.Mode)) return "未知插件包安装方式";
        if (request.Packages.Count > PluginPackageInstallRequest.MaxPackages)
            return $"一次最多安装 {PluginPackageInstallRequest.MaxPackages} 个插件包";
        foreach (var package in request.Packages)
        {
            if (!PluginPackageManifest.IsValidId(package.PluginId)) return $"插件 Id 无效：{package.PluginId}";
            if (FileDistributionRequest.ValidateTransfer(package.ToTransferRequest()) is { } error)
                return $"{package.PluginId}：{error}";
        }
        return request.Packages.GroupBy(x => x.PluginId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)
            ? "同一插件只能下发一个插件包"
            : null;
    }

    /// <summary>
    /// 按安装方式筛选插件包：安装模式跳过已安装的依赖；更新模式只保留已安装且包版本更高的插件（Force 时不比较版本）。
    /// </summary>
    internal static PackagePlan Plan(PluginPackageInstallRequest request, IReadOnlyDictionary<string, string> installed)
    {
        var apply = new List<PluginPackageRef>();
        var skipped = new List<SkippedPackage>();
        foreach (var package in request.Packages)
        {
            installed.TryGetValue(package.PluginId, out var current);
            string? reason = request.Mode switch
            {
                PluginPackageInstallMode.Install when package.IsDependency && current is not null =>
                    $"依赖已安装（{current}），跳过",
                PluginPackageInstallMode.Upgrade when current is null => "本机未安装该插件，跳过",
                PluginPackageInstallMode.Upgrade when !request.Force && !IsNewer(package.Version, current) =>
                    $"已是最新版本（{current}），跳过",
                _ => null,
            };
            if (reason is null) apply.Add(package);
            else skipped.Add(new SkippedPackage(package, reason));
        }
        return new PackagePlan(apply, skipped);
    }

    private static Dictionary<string, string> InstalledVersions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in IPluginService.LoadedPlugins)
            result[plugin.Manifest.Id] = plugin.Manifest.Version;
        return result;
    }

    /// <summary>
    /// 宿主插件包缓存目录：优先读取 ClassIsland.Services.PluginService.PluginsPkgRootPath，
    /// 宿主未公开该字段时使用与其相同的默认位置。
    /// </summary>
    private static string ResolvePackageRoot()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("ClassIsland.Services.PluginService", throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        if (type?.GetField("PluginsPkgRootPath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is string path &&
            !string.IsNullOrWhiteSpace(path))
            return path;
        return Path.Combine(CommonDirectories.AppCacheFolderPath, "PluginPackages");
    }

    private static bool IsNewer(string candidate, string? current) =>
        Version.TryParse(candidate, out var next) && (!Version.TryParse(current, out var installed) || next > installed);

    private static string DisplayName(PluginPackageRef package) =>
        string.IsNullOrWhiteSpace(package.Name) ? package.PluginId : package.Name;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };

    internal sealed record SkippedPackage(PluginPackageRef Package, string Reason);

    internal sealed record PackagePlan(IReadOnlyList<PluginPackageRef> Apply, IReadOnlyList<SkippedPackage> Skipped);
}

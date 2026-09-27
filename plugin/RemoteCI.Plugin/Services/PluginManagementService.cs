using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// ClassIsland 插件安装、卸载、启用和禁用。所有操作都通过宿主公开服务或 PluginInfo
/// 的公开状态接口完成，RemoteCI 不直接替换宿主插件目录。
/// </summary>
public sealed class PluginManagementService
{
    public const string RemoteCiPluginId = "remoteci.plugin";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private readonly string _policyPath;
    private readonly ILogger<PluginManagementService> _logger;

    public PluginManagementService(string pluginConfigFolder, ILogger<PluginManagementService> logger)
    {
        _policyPath = Path.Combine(pluginConfigFolder, "PluginManagementPolicy.json");
        _logger = logger;
    }

    public CommandResult SetPolicy(PluginManagementPolicyRequest? request)
    {
        if (request is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少插件管理策略");

        try
        {
            File.WriteAllText(_policyPath, JsonSerializer.Serialize(request, JsonDefaults.Options));
            return Success("远程插件管理策略已保存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存远程插件管理策略失败");
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"保存策略失败：{ex.Message}");
        }
    }

    public async Task<CommandResult> HandleAsync(CommandKind commandKind, PluginManagementRequest? request, CancellationToken ct = default)
    {
        if (request is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少插件管理参数");
        if (request.PluginIds.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "请至少选择一个插件");

        var ids = NormalizeIds(request.PluginIds);
        if (ids.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "插件 Id 不能为空");
        if (ids.Contains(RemoteCiPluginId, StringComparer.OrdinalIgnoreCase))
            return CommandResult.Failure(CommandResultCodes.Forbidden, "不能远程安装、卸载、启用或禁用 RemoteCI 自身");

        if (!IsActionAllowed(commandKind, request.Action))

            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "插件操作与命令类型不匹配");


        var policy = LoadPolicy();
        return request.Action switch
        {
            PluginActionKind.Install => await InstallAsync(ids, request.RestartAfter, policy, ct),
            PluginActionKind.Uninstall => Uninstall(ids, request.RestartAfter, policy),
            PluginActionKind.Enable => SetEnabled(ids, true, request.RestartAfter),
            PluginActionKind.Disable => SetEnabled(ids, false, request.RestartAfter),
            _ => CommandResult.Failure(CommandResultCodes.InvalidRequest, "未知插件操作"),
        };
    }

    private async Task<CommandResult> InstallAsync(
        IReadOnlyList<string> requestedIds,
        bool restartAfter,
        PluginManagementPolicyRequest policy,
        CancellationToken ct)
    {
        if (!policy.AllowRemoteInstall)
            return CommandResult.Failure(CommandResultCodes.Forbidden, "设备策略禁止远程安装插件");

        var market = IAppHost.TryGetService<IPluginMarketService>();
        if (market is null)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持插件市场");

        market.LoadPluginSource();
        var installed = IPluginService.LoadedPlugins
            .Select(plugin => plugin.Manifest.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        var missing = new List<string>();
        foreach (var id in requestedIds)
            ResolveDependencies(id, market, installed, resolved, missing);

        if (missing.Count > 0)
            return CommandResult.Failure(
                CommandResultCodes.InvalidRequest,
                $"缺少必选依赖：{string.Join("、", missing.Distinct(StringComparer.OrdinalIgnoreCase))}");

        var toDownload = resolved
            .Where(id => !installed.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toDownload.Count == 0)
            return Success("请求的插件均已安装");

        foreach (var id in toDownload)
            market.RequestDownloadPlugin(id);

        if (!restartAfter)
            return Success($"已开始下载 {toDownload.Count} 个插件，重启 ClassIsland 后生效");

        var downloadResult = await WaitForDownloadsAsync(market, toDownload, ct);
        if (!downloadResult.Success)
            return downloadResult;

        ScheduleRestart("插件安装完成，正在重启 ClassIsland");
        return Success($"已下载 {toDownload.Count} 个插件，ClassIsland 将自动重启");
    }

    private CommandResult Uninstall(IReadOnlyList<string> ids, bool restartAfter, PluginManagementPolicyRequest policy)
    {
        if (!policy.AllowRemoteUninstall)
            return CommandResult.Failure(CommandResultCodes.Forbidden, "设备策略禁止远程卸载插件");

        var plugins = IPluginService.LoadedPlugins
            .Where(plugin => ids.Contains(plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var found = plugins.Select(plugin => plugin.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"未找到插件：{string.Join("、", missing)}");

        foreach (var plugin in plugins)
            plugin.IsUninstalling = true;

        if (restartAfter)
            ScheduleRestart("插件卸载标记已写入，正在重启 ClassIsland");
        return Success($"已标记卸载 {plugins.Count} 个插件{(restartAfter ? "，ClassIsland 将自动重启" : "，重启后生效")}");
    }

    private CommandResult SetEnabled(IReadOnlyList<string> ids, bool enabled, bool restartAfter)
    {
        var plugins = IPluginService.LoadedPlugins
            .Where(plugin => ids.Contains(plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var found = plugins.Select(plugin => plugin.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"未找到插件：{string.Join("、", missing)}");

        foreach (var plugin in plugins)
            plugin.IsEnabled = enabled;

        var action = enabled ? "启用" : "禁用";
        if (restartAfter)
            ScheduleRestart($"插件已标记{action}，正在重启 ClassIsland");
        return Success($"已标记{action} {plugins.Count} 个插件{(restartAfter ? "，ClassIsland 将自动重启" : "，重启后生效")}");
    }

    private async Task<CommandResult> WaitForDownloadsAsync(
        IPluginMarketService market,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + DownloadTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var pending = 0;
            var failures = new List<string>();
            foreach (var id in ids)
            {
                if (!market.MergedPlugins.TryGetValue(id, out var plugin))
                {
                    pending++;
                    continue;
                }

                var progress = plugin.DownloadProgress;
                if (progress?.Exception is { } exception)
                {
                    failures.Add($"{id}：{exception.Message}");
                    continue;
                }

                if (progress?.IsDownloading == true || !plugin.RestartRequired)
                    pending++;
            }

            if (failures.Count > 0)
                return CommandResult.Failure(CommandResultCodes.InternalError, $"插件下载失败：{string.Join("；", failures)}");
            if (pending == 0)
                return Success("插件下载完成");

            await Task.Delay(250, ct);
        }

        return CommandResult.Failure(CommandResultCodes.Timeout, "等待插件下载超时，请稍后在 ClassIsland 插件页确认");
    }

    private void ResolveDependencies(
        string id,
        IPluginMarketService market,
        IReadOnlySet<string> installed,
        List<string> resolved,
        List<string> missing)
    {
        if (installed.Contains(id) || resolved.Contains(id, StringComparer.OrdinalIgnoreCase))
            return;

        var item = market.ResolveMarketPlugin(id);
        if (item is null)
        {
            if (!missing.Contains(id, StringComparer.OrdinalIgnoreCase))
                missing.Add(id);
            return;
        }

        resolved.Add(id);
        foreach (var dependency in item.Manifest.Dependencies)
        {
            if (installed.Contains(dependency.Id) || resolved.Contains(dependency.Id, StringComparer.OrdinalIgnoreCase))
                continue;
            if (market.ResolveMarketPlugin(dependency.Id) is null)
            {
                if (dependency.IsRequired && !missing.Contains(dependency.Id, StringComparer.OrdinalIgnoreCase))
                    missing.Add(dependency.Id);
                continue;
            }
            ResolveDependencies(dependency.Id, market, installed, resolved, missing);
        }
    }

    private PluginManagementPolicyRequest LoadPolicy()
    {
        try
        {
            if (!File.Exists(_policyPath)) return new PluginManagementPolicyRequest();
            return JsonSerializer.Deserialize<PluginManagementPolicyRequest>(File.ReadAllText(_policyPath), JsonDefaults.Options)
                ?? new PluginManagementPolicyRequest();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取远程插件管理策略失败，使用默认允许策略");
            return new PluginManagementPolicyRequest();
        }
    }

    private static bool IsActionAllowed(CommandKind commandKind, PluginActionKind action) => commandKind switch
    {
        CommandKind.InstallPlugins => action == PluginActionKind.Install,
        CommandKind.UninstallPlugins => action == PluginActionKind.Uninstall,
        CommandKind.SetPluginEnabled => action is PluginActionKind.Enable or PluginActionKind.Disable,
        _ => false,
    };

    private static List<string> NormalizeIds(IEnumerable<string> values) => values
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void ScheduleRestart(string reason)
    {
        // 先让命令回执经 WebSocket 发出，再延迟重启，避免发起端只看到连接断开。
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


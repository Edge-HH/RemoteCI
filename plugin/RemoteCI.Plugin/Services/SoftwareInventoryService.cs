using System.Reflection;
using System.Runtime.InteropServices;
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
/// 采集 ClassIsland 与插件版本，并通过宿主官方服务执行远程升级。
/// 插件包升级走插件市场缓存，ClassIsland 升级走官方 UpdateService；RemoteCI 不直接替换宿主文件。
/// </summary>
public sealed class SoftwareInventoryService
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(15);
    private readonly ILogger<SoftwareInventoryService> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _statusLock = new();
    private SoftwareUpdateStatus? _lastUpdate;

    public SoftwareInventoryService(ILogger<SoftwareInventoryService> logger)
    {
        _logger = logger;
    }

    /// <summary>版本清单发生变化时触发；RemoteCiService 监听后推送到服务端。</summary>
    public event Action? InventoryChanged;

    public SoftwareInventory Build()
    {
        var applications = new List<SoftwarePackageInfo>();
        var plugins = new List<SoftwarePackageInfo>();
        var appVersion = AppBase.AppVersion;
        var (appLatestVersion, appCanUpgrade) = GetClassIslandUpdateInfo();
        applications.Add(new SoftwarePackageInfo
        {
            Id = "classisland",
            Name = "ClassIsland",
            Version = appVersion,
            LatestVersion = appLatestVersion,
            IsUpdateAvailable = IsNewerVersion(appLatestVersion, appVersion),
            IsEnabled = true,
            CanUpgrade = appCanUpgrade,
        });

        try
        {
            var market = IAppHost.TryGetService<IPluginMarketService>();
            // LoadPluginSource 只读取宿主已经缓存的插件索引，不在每次版本上报时访问网络。
            market?.LoadPluginSource();
            foreach (var plugin in IPluginService.LoadedPlugins.OrderBy(x => x.Manifest.Name, StringComparer.CurrentCulture))
            {
                var id = plugin.Manifest.Id;
                var latestVersion = plugin.Manifest.Version;
                var isUpdateAvailable = false;
                if (market is not null && market.MergedPlugins.TryGetValue(id, out var merged))
                {
                    latestVersion = merged.Manifest.Version;
                    isUpdateAvailable = merged.IsUpdateAvailable;
                }

                plugins.Add(new SoftwarePackageInfo
                {
                    Id = id,
                    Name = plugin.Manifest.Name,
                    Version = plugin.Manifest.Version,
                    LatestVersion = latestVersion,
                    IsUpdateAvailable = isUpdateAvailable,
                    IsEnabled = IsPluginEnabled(plugin),
                    CanUpgrade = market is not null,
                });
            }
        }
        catch (Exception ex)
        {
            // 版本采集不能让云端心跳中断；把宿主能力缺失留给 canUpgrade 表达。
            _logger.LogDebug(ex, "读取 ClassIsland 插件清单失败");
        }

        return new SoftwareInventory
        {
            DeviceName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            GeneratedAt = DateTimeOffset.UtcNow,
            Applications = applications,
            Plugins = plugins,
            LastUpdate = GetLastUpdate(),
        };
    }

    /// <summary>刷新版本清单并通知上层发送；命令本身不等待网络下载。</summary>
    public CommandResult Refresh()
    {
        SetStatus(SoftwareUpdateOperation.InventoryRefresh, SoftwareUpdateState.Running, "正在读取本机软件版本");
        SetStatus(SoftwareUpdateOperation.InventoryRefresh, SoftwareUpdateState.Completed, "版本清单已刷新");
        RaiseInventoryChanged();
        return Success("版本清单已刷新");
    }

    /// <summary>启动插件批量升级；立即返回，实际下载和重启在后台完成。</summary>
    public CommandResult StartPluginUpgrade(SoftwareUpgradeRequest? request)
    {
        if (!_operationLock.Wait(0))
            return CommandResult.Failure(CommandResultCodes.Busy, "已有软件升级任务正在执行");
        if (IAppHost.TryGetService<IPluginMarketService>() is null)
        {
            _operationLock.Release();
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持插件市场升级");
        }

        var ids = request?.PluginIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _ = Task.Run(() => RunPluginUpgradeAsync(ids, request?.Force == true));
        return Success("已开始升级插件，下载完成后 ClassIsland 将自动重启");
    }

    /// <summary>启动 ClassIsland 主程序升级；立即返回，实际下载和部署在后台完成。</summary>
    public CommandResult StartClassIslandUpgrade(SoftwareUpgradeRequest? request)
    {
        if (!_operationLock.Wait(0))
            return CommandResult.Failure(CommandResultCodes.Busy, "已有软件升级任务正在执行");
        var updateService = TryGetClassIslandUpdateService();
        if (updateService is null)
        {
            _operationLock.Release();
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持应用升级");
        }

        _ = Task.Run(() => RunClassIslandUpgradeAsync(updateService, request?.Force == true));
        return Success("已开始升级 ClassIsland，下载并部署完成后将自动重启");
    }

    private async Task RunPluginUpgradeAsync(IReadOnlyCollection<string>? requestedIds, bool force)
    {
        try
        {
            SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Running, "正在刷新插件市场索引");
            var market = IAppHost.GetService<IPluginMarketService>();
            await market.RefreshPluginSourceAsync();
            market.LoadPluginSource();
            var targetIds = SelectPluginUpgradeIds(market, requestedIds, force);
            if (targetIds.Count == 0)
            {
                SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Completed, "所有插件均已是最新版本");
                RaiseInventoryChanged();
                return;
            }

            SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Running,
                $"正在下载 {targetIds.Count} 个插件更新");
            foreach (var id in targetIds)
                market.RequestDownloadPlugin(id);

            var deadline = DateTimeOffset.UtcNow + UpdateTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = GetPluginDownloadState(market, targetIds);
                if (state.Pending == 0)
                {
                    await CompletePluginUpgradeAsync(state.Succeeded, state.Failed);
                    return;
                }
                await Task.Delay(500);
            }

            SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Failed, "插件更新下载超时");
            RaiseInventoryChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "远程升级插件失败");
            SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Failed,
                $"插件升级失败：{ex.Message}");
            RaiseInventoryChanged();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static HashSet<string> SelectPluginUpgradeIds(
        IPluginMarketService market,
        IReadOnlyCollection<string>? requestedIds,
        bool force)
    {
        var requested = requestedIds is { Count: > 0 }
            ? new HashSet<string>(requestedIds, StringComparer.Ordinal)
            : null;
        return market.MergedPlugins.Values
            .Where(plugin => plugin.IsLocal && plugin.IsEnabled)
            .Where(plugin => requested is null || requested.Contains(plugin.Manifest.Id))
            .Where(plugin => force || plugin.IsUpdateAvailable)
            .Select(plugin => plugin.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (int Pending, int Succeeded, int Failed) GetPluginDownloadState(
        IPluginMarketService market,
        IEnumerable<string> targetIds)
    {
        var pending = 0;
        var succeeded = 0;
        var failed = 0;
        foreach (var id in targetIds)
        {
            if (!market.MergedPlugins.TryGetValue(id, out var plugin) ||
                plugin.DownloadProgress?.Exception is not null)
            {
                failed++;
            }
            else if (plugin.RestartRequired)
            {
                succeeded++;
            }
            else
            {
                // 下载任务尚未建立或仍在进行中，继续等待官方市场服务完成。
                pending++;
            }
        }
        return (pending, succeeded, failed);
    }

    private async Task CompletePluginUpgradeAsync(int succeeded, int failed)
    {
        if (succeeded > 0)
        {
            SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Completed,
                $"已下载 {succeeded} 个插件更新，ClassIsland 即将重启");
            RaiseInventoryChanged();
            await Task.Delay(750);
            RestartClassIslandAfterPluginUpdate();
            return;
        }

        SetStatus(SoftwareUpdateOperation.Plugins, SoftwareUpdateState.Failed,
            $"插件更新失败，失败 {failed} 个");
        RaiseInventoryChanged();
    }

    private async Task RunClassIslandUpgradeAsync(object updateService, bool force)
    {
        try
        {
            SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Running, "正在检查 ClassIsland 更新");
            await InvokeTaskAsync(updateService, "CheckUpdateAsync", force, false);
            if (GetProperty<Exception>(updateService, "NetworkErrorException") is { } checkError)
                throw new InvalidOperationException(checkError.Message, checkError);

            var (latestVersion, _) = GetClassIslandUpdateInfo(updateService);
            var currentVersion = AppBase.AppVersion;
            if (!force && !IsNewerVersion(latestVersion, currentVersion))
            {
                SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Completed, "ClassIsland 已是最新版本");
                RaiseInventoryChanged();
                return;
            }

            SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Running, "正在下载 ClassIsland 更新");
            await InvokeTaskAsync(updateService, "DownloadUpdateAsync");
            if (GetProperty<Exception>(updateService, "NetworkErrorException") is { } downloadError)
                throw new InvalidOperationException(downloadError.Message, downloadError);

            SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Running, "正在部署 ClassIsland 更新");
            await InvokeTaskAsync(updateService, "ExtractUpdateAsync");
            if (GetProperty<Exception>(updateService, "DeployErrorException") is { } deployError)
                throw new InvalidOperationException(deployError.Message, deployError);

            SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Completed,
                "ClassIsland 更新已部署，即将自动重启");
            RaiseInventoryChanged();
            await Task.Delay(750);
            RestartClassIslandAfterAppUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "远程升级 ClassIsland 失败");
            SetStatus(SoftwareUpdateOperation.ClassIsland, SoftwareUpdateState.Failed,
                $"ClassIsland 升级失败：{ex.Message}");
            RaiseInventoryChanged();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private SoftwareUpdateStatus? GetLastUpdate()
    {
        lock (_statusLock)
            return _lastUpdate;
    }

    private void SetStatus(SoftwareUpdateOperation operation, SoftwareUpdateState state, string message)
    {
        lock (_statusLock)
        {
            var now = DateTimeOffset.UtcNow;
            var startedAt = _lastUpdate is { } previous && previous.Operation == operation
                ? previous.StartedAt
                : now;
            _lastUpdate = new SoftwareUpdateStatus
            {
                Operation = operation,
                State = state,
                Message = message,
                StartedAt = startedAt,
                CompletedAt = state is SoftwareUpdateState.Completed or SoftwareUpdateState.Failed
                    ? now
                    : null,
            };
        }
    }

    private void RaiseInventoryChanged()
    {
        try
        {
            InventoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知软件版本变化失败");
        }
    }

    private static bool IsPluginEnabled(PluginInfo plugin)
    {
        try { return plugin.IsEnabled; }
        catch { return true; }
    }

    private (string? LatestVersion, bool CanUpgrade) GetClassIslandUpdateInfo(object? updateService = null)
    {
        updateService ??= TryGetClassIslandUpdateService();
        if (updateService is null) return (null, false);

        var latestVersion = GetProperty<object>(updateService, "DistributionInfo") is { } info
            ? GetProperty<string>(info, "Version")
            : null;
        var allowedTypes = GetStaticField<string[]>(updateService.GetType(), "AllowedPackageTypes");
        var canUpgrade = allowedTypes is null || allowedTypes.Contains(AppBase.Current.PackagingType, StringComparer.Ordinal);
        return (string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion.Trim(), canUpgrade);
    }

    private static object? TryGetClassIslandUpdateService()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("ClassIsland.Services.AppUpdating.UpdateService", throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        return type is null ? null : IAppHost.Host?.Services.GetService(type);
    }

    private static async Task InvokeTaskAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        try
        {
            if (method.Invoke(target, args) is Task task)
                await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static T? GetProperty<T>(object target, string propertyName)
    {
        var value = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        return value is T typed ? typed : default;
    }

    private static T? GetStaticField<T>(Type type, string fieldName) where T : class =>
        type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as T;

    private static bool IsNewerVersion(string? latest, string current) =>
        Version.TryParse(latest, out var latestVersion) &&
        Version.TryParse(current, out var currentVersion) &&
        latestVersion > currentVersion;

    private static void RestartClassIslandAfterPluginUpdate() =>
        Dispatcher.UIThread.Post(() => AppBase.Current.Restart());

    private static void RestartClassIslandAfterAppUpdate() =>
        Dispatcher.UIThread.Post(() => AppBase.Current.Restart(new[] { "-m" }, true));

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}



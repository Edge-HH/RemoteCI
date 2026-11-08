using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Management;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 把服务端下发的班级策略写入 ClassIsland 的 ManagementPolicy，宿主各界面据此禁用编辑、调试与退出入口。
/// 设备同时加入 ClassIsland 内置集控时只加严不放松：以集控自己的策略为下限取并集，
/// 避免 RemoteCI 的“允许”覆盖集控服务器已有的限制；集控重载策略后自动重新应用。
/// </summary>
public sealed class ClassPolicyService(
    ClassPolicyStore store,
    PluginsSettingsPageGuard pluginsPage,
    ILogger<ClassPolicyService> logger)
{
    /// <summary>内置集控重载后宿主会换一个新的策略对象；按此间隔检查并重新应用缓存的班级策略。</summary>
    private static readonly TimeSpan HostPolicyWatchInterval = TimeSpan.FromSeconds(30);

    /// <summary>上次写入的宿主策略对象；宿主换成新对象时据此识别并重新取下限。</summary>
    private ManagementPolicy? _target;

    /// <summary><see cref="_target"/> 在 RemoteCI 首次写入前的取值，即内置集控自己的限制。</summary>
    private ManagementPolicy? _baseline;

    private DispatcherTimer? _hostWatch;

    public ClassPolicy? Current => store.Current;

    /// <summary>启动时应用缓存的策略；从未收到过策略时不改动宿主。</summary>
    public void ApplyStored()
    {
        if (store.Current is not { } policy) return;
        _ = ApplyAsync(policy, persist: false);
    }

    /// <summary>命令与同步入口：保存策略并立即生效。</summary>
    public async Task<CommandResult> ApplyAsync(ClassPolicy? policy, bool persist = true)
    {
        if (policy is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少班级策略");
        if (persist) store.Apply(policy);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ApplyToHost(policy));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "应用班级策略失败");
            return CommandResult.Failure(CommandResultCodes.InternalError, $"应用班级策略失败：{ex.Message}");
        }
    }

    private CommandResult ApplyToHost(ClassPolicy policy)
    {
        // “禁止插件设置”不属于 ClassIsland 集控，由插件自己执行，不依赖 IManagementService。
        pluginsPage.Apply(policy.DisablePluginsSettings);

        var management = IAppHost.TryGetService<IManagementService>();
        if (management is null)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持集控策略");

        ApplyToPolicy(management.Policy, management.IsManagementEnabled, policy);
        EnsureHostWatch();
        return Success(policy.IsDefault ? "已恢复默认策略" : "班级策略已生效");
    }

    /// <summary>
    /// 写入宿主当前的策略对象。下限只在第一次见到某个对象时取：同一对象已被 RemoteCI 改过，
    /// 再取会把 RemoteCI 自己的限制当成集控的限制，之后就再也解除不了。
    /// 内置集控收到 DataUpdated 后会从文件重新加载出新对象，新对象只含集控自己的值，此时重新取下限。
    /// </summary>
    internal void ApplyToPolicy(ManagementPolicy target, bool managementEnabled, ClassPolicy policy)
    {
        if (!ReferenceEquals(target, _target))
        {
            _target = target;
            _baseline = Snapshot(target);
        }
        ApplyTo(target, managementEnabled ? _baseline : null, policy);
    }

    /// <summary>宿主替换策略对象时没有事件，只能定时比对引用；只在已应用过策略后启动。</summary>
    private void EnsureHostWatch()
    {
        if (_hostWatch is not null) return;
        _hostWatch = new DispatcherTimer { Interval = HostPolicyWatchInterval };
        _hostWatch.Tick += (_, _) => ReapplyIfHostPolicyReplaced();
        _hostWatch.Start();
    }

    private void ReapplyIfHostPolicyReplaced()
    {
        try
        {
            if (store.Current is not { } policy) return;
            if (IAppHost.TryGetService<IManagementService>() is not { } management) return;
            if (ReferenceEquals(management.Policy, _target)) return;
            logger.LogInformation("ClassIsland 集控策略已重新加载，重新应用班级策略");
            ApplyToPolicy(management.Policy, management.IsManagementEnabled, policy);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "重新应用班级策略失败");
        }
    }

    /// <summary>
    /// 把 RemoteCI 策略写入宿主策略。baseline 为 null（未启用内置集控）时按目标值设置，允许解除；
    /// 否则只加严：限制类取并集，允许类取交集。
    /// </summary>
    internal static void ApplyTo(ManagementPolicy target, ManagementPolicy? baseline, ClassPolicy policy)
    {
        var restrictOnly = baseline is not null;
        target.DisableProfileClassPlanEditing = Disable(policy.DisableProfileClassPlanEditing,
            baseline?.DisableProfileClassPlanEditing ?? false, restrictOnly);
        target.DisableProfileTimeLayoutEditing = Disable(policy.DisableProfileTimeLayoutEditing,
            baseline?.DisableProfileTimeLayoutEditing ?? false, restrictOnly);
        target.DisableProfileSubjectsEditing = Disable(policy.DisableProfileSubjectsEditing,
            baseline?.DisableProfileSubjectsEditing ?? false, restrictOnly);
        target.DisableProfileEditing = Disable(policy.DisableProfileEditing,
            baseline?.DisableProfileEditing ?? false, restrictOnly);
        target.DisableSettingsEditing = Disable(policy.DisableSettingsEditing,
            baseline?.DisableSettingsEditing ?? false, restrictOnly);
        target.DisableSplashCustomize = Disable(policy.DisableSplashCustomize,
            baseline?.DisableSplashCustomize ?? false, restrictOnly);
        target.DisableDebugMenu = Disable(policy.DisableDebugMenu,
            baseline?.DisableDebugMenu ?? false, restrictOnly);
        target.AllowExitManagement = Allow(policy.AllowExitManagement,
            baseline?.AllowExitManagement ?? true, restrictOnly);
    }

    /// <summary>限制类开关：集控启用时取并集，否则按 RemoteCI 的值设置（可解除）。</summary>
    private static bool Disable(bool desired, bool baseline, bool restrictOnly) =>
        restrictOnly ? baseline || desired : desired;

    /// <summary>允许类开关：集控启用时取交集，否则按 RemoteCI 的值设置（可恢复）。</summary>
    private static bool Allow(bool desired, bool baseline, bool restrictOnly) =>
        restrictOnly ? baseline && desired : desired;

    internal static ManagementPolicy Snapshot(ManagementPolicy source) => new()
    {
        DisableProfileClassPlanEditing = source.DisableProfileClassPlanEditing,
        DisableProfileTimeLayoutEditing = source.DisableProfileTimeLayoutEditing,
        DisableProfileSubjectsEditing = source.DisableProfileSubjectsEditing,
        DisableProfileEditing = source.DisableProfileEditing,
        DisableSettingsEditing = source.DisableSettingsEditing,
        DisableSplashCustomize = source.DisableSplashCustomize,
        DisableDebugMenu = source.DisableDebugMenu,
        AllowExitManagement = source.AllowExitManagement,
        DisableEasterEggs = source.DisableEasterEggs,
    };

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}

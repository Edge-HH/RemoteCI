using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Management;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 把服务端下发的班级策略写入 ClassIsland 的 ManagementPolicy，宿主各界面据此禁用编辑、调试与退出入口。
/// 设备同时加入 ClassIsland 内置集控时只加严不放松：在插件启动时的策略基础上取并集，
/// 避免 RemoteCI 的“允许”覆盖集控服务器已有的限制。
/// </summary>
public sealed class ClassPolicyService(ClassPolicyStore store, ILogger<ClassPolicyService> logger)
{
    private ManagementPolicy? _baseline;
    private bool _baselineIsManaged;

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
        var management = IAppHost.TryGetService<IManagementService>();
        if (management is null)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持集控策略");

        var target = management.Policy;
        // 内置集控启用时，以插件启动时宿主已有的策略为下限，只允许 RemoteCI 继续加严。
        if (_baseline is null || _baselineIsManaged != management.IsManagementEnabled)
        {
            _baseline = Snapshot(target);
            _baselineIsManaged = management.IsManagementEnabled;
        }
        ApplyTo(target, management.IsManagementEnabled ? _baseline : null, policy);
        return Success(policy.IsDefault ? "已恢复默认策略" : "班级策略已生效");
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

using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Views.SettingsPages;

namespace RemoteCI.Plugin;

/// <summary>
/// RemoteCI 插件入口。职责：加载/保存配置、注册服务与设置页、
/// 在应用启动完成后启动 RemoteCiService。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>当前插件实例（供设置页访问）。</summary>
    public static Plugin? Current { get; private set; }

    /// <summary>插件配置。</summary>
    public PluginSettings Settings { get; private set; } = new();

    private RemoteCiService? _service;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Current = this;

        // 加载配置；配置目录由 ClassIsland 提供，保存在插件安装目录之外，更新插件不会丢失。
        var settingsPath = Path.Combine(PluginConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);
        // 长期凭据不随 Settings.json 明文落盘：从 DPAPI 存储加载，兼容迁移旧版明文字段。
        var tokenStore = new CloudTokenStore(Path.Combine(PluginConfigFolder, "CloudToken.bin"));
        Settings.CloudToken = tokenStore.Load();
        var migratedToken = Settings.CloudToken is null
            ? CloudTokenStore.TryMigrateLegacyPlaintext(settingsPath)
            : null;
        Settings.CloudToken ??= migratedToken;
        if (Settings.CloudToken is not null) tokenStore.Save(Settings.CloudToken);
        if (migratedToken is not null)
        {
            // 迁移成功后立即重写 Settings.json 清除旧版明文字段（CloudToken 已标记 JsonIgnore），
            // 不能等用户下次修改设置才触发保存，否则明文凭据会无限期残留磁盘。
            ConfigureFileHelper.SaveConfig(settingsPath, Settings);
            FileProtection.RestrictToCurrentUser(settingsPath);
        }
        Settings.PropertyChanged += (_, _) =>
        {
            ConfigureFileHelper.SaveConfig(settingsPath, Settings);
            FileProtection.RestrictToCurrentUser(settingsPath);
        };

        services.AddSingleton(Settings);
        services.AddSingleton(tokenStore);
        services.AddSingleton(new AccountMirror(Path.Combine(PluginConfigFolder, "Accounts.json")));
        // 课表读取防腐层:隔离 ClassIsland 服务接口(含 internal 成员),使核心逻辑可单元测试。
        services.AddSingleton<IScheduleBackend, ScheduleBackendAdapter>();
        services.AddSingleton<IProfileWriteOperations, ProfileWriteAdapter>();
        services.AddSingleton<IStateSource, StateSourceAdapter>();
        services.AddSingleton<ScheduleCatalog>();
        services.AddSingleton<ClassIslandHostControlService>();
        services.AddSingleton<VoiceMessagePlayer>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<ClassIslandNotificationBridge>();
        services.AddSingleton<StateCollector>();
        services.AddSingleton<ScheduleSyncTaskCoordinator>();
        services.AddSingleton<RemoteCiService>();
        // 公开扩展注册表：其他 ClassIsland 插件在 AppStarted 后通过 IAppHost.GetService<IRemoteCiExtensionRegistry>() 获取并注册自定义远程功能。
        services.AddSingleton<IRemoteCiExtensionRegistry, RemoteCiExtensionRegistry>();
        services.AddNotificationProvider<RemoteNotificationProvider>();
        services.AddSettingsPage<RemoteCiSettingsPage>();
        // 设置页注册表在 ClassIsland 启动时构建，因此开关保存后需重启才会改变菜单。
        if (ShouldRegisterDeveloperSettingsPage(Settings))
            services.AddSettingsPage<RemoteCiDeveloperSettingsPage>();

        var app = AppBase.Current;
        app.AppStarted += (_, _) =>
        {
            try
            {
                _service = IAppHost.GetService<RemoteCiService>();
                _service.Start();
            }
            catch (Exception ex)
            {
                // 端口冲突或配置损坏时插件启动失败，绝不能中断 ClassIsland 自身的启动流程。
                try
                {
                    IAppHost.GetService<ILoggerFactory>()
                        .CreateLogger("RemoteCI.Plugin")
                        .LogError(ex, "RemoteCI 服务启动失败，插件将在本次运行中不可用");
                }
                catch
                {
                    Console.Error.WriteLine($"RemoteCI 服务启动失败：{ex}");
                }
            }
        };
        app.AppStopping += (_, _) => _service?.Stop();
    }

    internal static bool ShouldRegisterDeveloperSettingsPage(PluginSettings settings) =>
        settings.ShowDeveloperSettingsMenu;
}

namespace RemoteCI.Shared;

/// <summary>三端共同使用的 V3 协议常量。</summary>
public static class Protocol
{
    public const int Version = 3;

    public const string MessageTypeStatePush = "state_push";
    public const string MessageTypeScheduleSync = "schedule_sync";
    public const string MessageTypeSchedulePull = "schedule_pull";
    public const string MessageTypeScheduleSyncStatus = "schedule_sync_status";
    public const string MessageTypeEventNotify = "event_notify";
    public const string MessageTypeCommand = "command";
    public const string MessageTypeCommandResult = "command_result";
    public const string MessageTypeAuthChallenge = "auth_challenge";
    public const string MessageTypeAuthProof = "auth_proof";
    public const string MessageTypeAuthState = "auth_state";
    public const string MessageTypeAccountSync = "account_sync";
    public const string MessageTypeExtensionsSync = "extensions_sync";
    /// <summary>插件向服务端同步扩展分组、设置字段与设备当前设置值；手表与局域网不使用。</summary>
    public const string MessageTypeExtensionGroupsSync = "extension_groups_sync";
    public const string MessageTypeSettingsSync = "settings_sync";
    public const string MessageTypePluginNetworkInfo = "plugin_network_info";
    public const string MessageTypeConnectionBootstrap = "connection_bootstrap";
    public const string MessageTypePeerCapabilities = "peer_capabilities";
    public const string MessageTypeCapabilitiesSync = "capabilities_sync";
    public const string MessageTypeSoftwareInventory = "software_inventory";
    /// <summary>服务端发给某个用户全部在线手机/手表连接的个人通知（例如换课申请），不按班级过滤。</summary>
    public const string MessageTypeUserNotify = "user_notify";
    /// <summary>服务端发给插件的调休日历（放假日与调休上学日），插件据此开关课表、建立调休临时课表。</summary>
    public const string MessageTypeHolidayCalendar = "holiday_calendar";

    public const int LanDiscoveryPort = 48765;
    public const string LanDiscoveryRequest = "REMOTECI_DISCOVER_V3";

    public const string QueryToken = "token";
    public const string HeaderAuthorization = "Authorization";
    public const string BearerScheme = "Bearer";
}

public enum PeerRole
{
    Plugin = 1,
    Watch = 2,
    Mobile = 3,
}

public enum UserRole
{
    User = 1,
    Admin = 2,
}

/// <summary>
/// 有效权限位。管理员的有效权限固定为 All；普通用户至少拥有 ViewCurrentCourse。
/// </summary>
[Flags]
public enum UserPermissions
{
    None = 0,
    ViewCurrentCourse = 1 << 0,
    AccessWebUi = 1 << 1,
    ManageUsers = 1 << 2,
    SendNotifications = 1 << 3,
    ManageSchedule = 1 << 4,
    PowerControl = 1 << 5,
    // 保留旧名称供现有第三方扩展源码兼容；新代码应使用 PowerControl。
    SystemControl = PowerControl,
    TeacherComing = 1 << 6,
    RunExtensions = 1 << 7,
    MainMenuControl = 1 << 8,
    SendVoiceMessages = 1 << 9,
    // 修改自己的用户可见用户名（DisplayName）；不改变唯一登录 ID。
    ChangeDisplayName = 1 << 10,
    // 允许使用 API Key 调用服务端 REST API；API Key 仍会按账号当前权限逐次鉴权。
    ApiAccess = 1 << 11,
    // 老师主动发起换课申请（临时换课），由对方老师审批。
    RequestScheduleSwap = 1 << 12,
    // 不经审批直接强制换课；对方老师可撤回。
    ForceScheduleSwap = 1 << 13,
    All = ViewCurrentCourse | AccessWebUi | ManageUsers | SendNotifications | ManageSchedule |
          PowerControl | TeacherComing | RunExtensions | MainMenuControl | SendVoiceMessages |
          ChangeDisplayName | ApiAccess | RequestScheduleSwap | ForceScheduleSwap,
}

public static class RolePermissions
{
    /// <summary>可授予普通账号或自定义角色的权限集合。</summary>
    public const UserPermissions Assignable = UserPermissions.AccessWebUi | UserPermissions.ManageUsers |
        UserPermissions.SendNotifications | UserPermissions.ManageSchedule | UserPermissions.PowerControl |
        UserPermissions.TeacherComing | UserPermissions.RunExtensions | UserPermissions.MainMenuControl |
        UserPermissions.SendVoiceMessages | UserPermissions.ApiAccess | UserPermissions.RequestScheduleSwap |
        UserPermissions.ForceScheduleSwap;

    public static UserPermissions Effective(
        UserRole role,
        UserPermissions granted,
        UserPermissions roleDefaults = UserPermissions.None) => role == UserRole.Admin
        ? UserPermissions.All
        : UserPermissions.ViewCurrentCourse |
          (roleDefaults & ~UserPermissions.ViewCurrentCourse) |
          (granted & ~UserPermissions.ViewCurrentCourse);

    public static bool Has(UserProfileLike user, UserPermissions permission) =>
        (Effective(user.Role, user.GrantedPermissions) & permission) == permission;
}

/// <summary>供共享权限计算使用的小接口，避免服务端实体依赖传输模型。</summary>
public interface UserProfileLike
{
    UserRole Role { get; }
    UserPermissions GrantedPermissions { get; }
}

public enum ClassStateKind
{
    None = 0,
    Class = 1,
    Breaking = 2,
    AfterSchool = 3,
    PrepareClass = 4,
}

public enum ClassEventKind
{
    OnClass = 1,
    OnBreaking = 2,
    OnAfterSchool = 3,
    ScheduleChanged = 4,
    Custom = 5,
    AutomationNotification = 6,
    PluginNotification = 7,
}

public enum CommandKind
{
    ChangeSchedule = 1,
    SendNotification = 2,
    ClearNotifications = 3,
    SetMainMenuVisibility = 4,
    Power = 5,
    Volume = 6,
    /// <summary>执行其他 ClassIsland 插件通过 RemoteCI 注册的自定义远程功能。</summary>
    RunExtension = 7,
    /// <summary>显示“老师来了”强调提醒，等待 1 秒后由插件自动清除。</summary>
    TeacherComing = 8,
    SendVoiceMessage = 9,
    /// <summary>升级 ClassIsland 插件；升级包由宿主插件市场处理，重启后生效。</summary>
    UpgradePlugins = 10,
    /// <summary>升级 ClassIsland 主程序；由宿主官方更新服务下载并部署。</summary>
    UpgradeClassIsland = 11,
    /// <summary>请求插件重新采集并上报应用与插件版本清单。</summary>
    RefreshSoftwareInventory = 12,
    /// <summary>通过 ClassIsland 插件市场下载并安装一组插件，重启后生效。</summary>
    InstallPlugins = 13,
    /// <summary>卸载一组本地插件，重启后生效。</summary>
    UninstallPlugins = 14,
    /// <summary>启用或禁用一组本地插件，重启后生效。</summary>
    SetPluginEnabled = 15,
    /// <summary>设置 RemoteCI 远程插件管理策略。</summary>
    SetPluginManagementPolicy = 16,
    /// <summary>把档案 JSON 中的时间表、课表或科目分发到设备。</summary>
    DistributeProfile = 17,
    /// <summary>新增或整体替换一张 ClassIsland 时间表。</summary>
    UpdateTimeLayout = 18,
    /// <summary>让设备加入 ClassIsland 内置集控。</summary>
    JoinManagement = 19,
    /// <summary>仅重启 ClassIsland 宿主，不重启 Windows。</summary>
    RestartClassIsland = 20,
    /// <summary>设置班级某科目的授课教师名，写入 ClassIsland 档案并随课表推送生效。</summary>
    SetSubjectTeacher = 21,
    /// <summary>在设备上执行一条远程终端命令并返回标准输出/错误；无状态，等价于 cmd /d /c。</summary>
    ExecuteTerminalCommand = 22,
    /// <summary>把一个文件分发到设备的桌面、下载或文档文件夹；文件名净化，默认不覆盖。</summary>
    SendFile = 23,
    /// <summary>修改某个扩展分组在设备上的设置（部分更新），由注册方插件实际写入并生效。</summary>
    ApplyExtensionSettings = 24,
    /// <summary>应用服务端保存并验证过的档案；仅档案管理页面可以发起。</summary>
    ApplyProfile = 25,
}

public enum PowerActionKind
{
    Shutdown = 1,
    Restart = 2,
    Sleep = 3,
    Hibernate = 4,
}

public static class CommandPermissions
{
    public static UserPermissions Required(CommandKind command) => command switch
    {
        CommandKind.ChangeSchedule or CommandKind.SetSubjectTeacher or CommandKind.ApplyProfile => UserPermissions.ManageSchedule,
        CommandKind.SendNotification or CommandKind.ClearNotifications => UserPermissions.SendNotifications,
        CommandKind.SendVoiceMessage => UserPermissions.SendVoiceMessages,
        CommandKind.TeacherComing => UserPermissions.TeacherComing,
        CommandKind.SetMainMenuVisibility => UserPermissions.MainMenuControl,
        CommandKind.Power or CommandKind.Volume => UserPermissions.PowerControl,
        // 服务端另按“系统管理员或获准的班管理员”复核，插件端只校验扩展权限位作为纵深防御。
        CommandKind.ApplyExtensionSettings => UserPermissions.RunExtensions,
        // 远程升级、插件管理、集控、终端与文件分发都会改变教室端程序、配置或文件系统，
        // 与账号管理同属高风险管理员操作。
        CommandKind.UpgradePlugins or CommandKind.UpgradeClassIsland or CommandKind.RefreshSoftwareInventory or
        CommandKind.InstallPlugins or CommandKind.UninstallPlugins or CommandKind.SetPluginEnabled or
        CommandKind.SetPluginManagementPolicy or CommandKind.DistributeProfile or CommandKind.UpdateTimeLayout or
        CommandKind.JoinManagement
        or CommandKind.RestartClassIsland
        or CommandKind.ExecuteTerminalCommand or CommandKind.SendFile
            => UserPermissions.ManageUsers,
        _ => UserPermissions.None,
    };
}

/// <summary>V3 协议内稳定、可追加的功能能力标识。</summary>
public static class RemoteCiCapabilities
{
    public const string ClassStateRead = "class-state.read";
    public const string ScheduleRead = "schedule.read";
    public const string SchedulePull = "schedule.pull";
    public const string ScheduleChange = "schedule.change";
    public const string NotificationSend = "notification.send";
    public const string VoiceMessageSend = "voice-message.send";
    public const string NotificationClear = "notification.clear";
    public const string TeacherComing = "teacher-coming";
    public const string MainMenuVisibility = "main-menu.visibility";
    public const string PowerControl = "power.control";
    public const string VolumeControl = "volume.control";
    public const string ExtensionsRun = "extensions.run";
    public const string SoftwareInventory = "software.inventory";
    public const string SoftwareUpgradePlugins = "software.upgrade-plugins";
    public const string SoftwareUpgradeClassIsland = "software.upgrade-classisland";
    public const string PluginInstall = "plugin.install";
    public const string PluginUninstall = "plugin.uninstall";
    public const string PluginEnable = "plugin.enable";
    public const string PluginManagementPolicy = "plugin.management-policy";
    public const string ProfileDistribute = "profile.distribute";
    public const string TimeLayoutUpdate = "schedule.time-layout";
    public const string ManagementJoin = "management.join";
    /// <summary>把换课写入 ClassIsland 源课表（本周及以后每周生效），而不是只写到当天临时课表层。</summary>
    public const string ScheduleChangePermanent = "schedule.change-permanent";
    /// <summary>设置班级科目的授课教师（写入 ClassIsland 档案）。</summary>
    public const string ScheduleSubjectTeacher = "schedule.subject-teacher";
    /// <summary>在设备上执行远程终端命令并取回输出。</summary>
    public const string TerminalExecute = "terminal.execute";
    /// <summary>把文件分发到设备的用户文件夹。</summary>
    public const string FileDistribute = "file.distribute";
    /// <summary>同步扩展分组与设置页，并接受远程修改扩展设置。</summary>
    public const string ExtensionsSettings = "extensions.settings";
    /// <summary>按明确选择的方式更新、替换或创建并启用服务端档案。</summary>
    public const string ProfileApply = "profile.apply";
    /// <summary>接收调休日历并在放假日关闭课表、调休上学日建立临时课表。</summary>
    public const string HolidayCalendar = "schedule.holiday-calendar";

    /// <summary>没有上报能力列表的旧 V3 端自动获得的基础能力。</summary>
    public static IReadOnlyList<string> Baseline { get; } =
    [
        ClassStateRead,
        ScheduleRead,
        SchedulePull,
        ScheduleChange,
        NotificationSend,
        NotificationClear,
        TeacherComing,
        MainMenuVisibility,
        PowerControl,
        VolumeControl,
        ExtensionsRun,
    ];

    /// <summary>当前版本支持的能力；新能力不能加入旧端默认获得的 Baseline。</summary>
    public static IReadOnlyList<string> Current { get; } =
        [.. Baseline, VoiceMessageSend, SoftwareInventory, SoftwareUpgradePlugins, SoftwareUpgradeClassIsland,
            PluginInstall, PluginUninstall, PluginEnable, PluginManagementPolicy, ProfileDistribute, TimeLayoutUpdate, ManagementJoin,
            ScheduleSubjectTeacher, TerminalExecute, FileDistribute, ExtensionsSettings, ProfileApply, HolidayCalendar];

    /// <summary>面向管理员诊断界面的中文说明；未知标识仍保留原值并标注为未知能力。</summary>
    public static string ChineseName(string capability) => capability switch
    {
        ClassStateRead => "读取课堂状态",
        ScheduleRead => "读取课表",
        SchedulePull => "拉取课表",
        ScheduleChange => "修改课表",
        NotificationSend => "发送通知",
        VoiceMessageSend => "发送语音消息",
        NotificationClear => "清除通知",
        TeacherComing => "老师来了",
        MainMenuVisibility => "控制主界面显示",
        PowerControl => "电源控制",
        VolumeControl => "音量控制",
        ExtensionsRun => "运行扩展功能",
        SoftwareInventory => "读取软件版本",
        SoftwareUpgradePlugins => "升级插件",
        SoftwareUpgradeClassIsland => "升级 ClassIsland",
        PluginInstall => "安装插件",
        PluginUninstall => "卸载插件",
        PluginEnable => "启用或禁用插件",
        PluginManagementPolicy => "远程插件管理策略",
        ProfileDistribute => "分发档案",
        TimeLayoutUpdate => "修改时间表",
        ManagementJoin => "加入集控",
        ScheduleSubjectTeacher => "设置科目教师",
        TerminalExecute => "远程终端",
        FileDistribute => "文件分发",
        ExtensionsSettings => "修改扩展设置",
        ProfileApply => "应用服务端档案",
        HolidayCalendar => "调休自动适配",
        _ => "未知能力",
    };

    public static string? Required(CommandKind command) => command switch
    {
        CommandKind.ChangeSchedule => ScheduleChange,
        CommandKind.SendNotification => NotificationSend,
        CommandKind.SendVoiceMessage => VoiceMessageSend,
        CommandKind.ClearNotifications => NotificationClear,
        CommandKind.TeacherComing => TeacherComing,
        CommandKind.SetMainMenuVisibility => MainMenuVisibility,
        CommandKind.Power => PowerControl,
        CommandKind.Volume => VolumeControl,
        CommandKind.RunExtension => ExtensionsRun,
        CommandKind.RefreshSoftwareInventory => SoftwareInventory,
        CommandKind.UpgradePlugins => SoftwareUpgradePlugins,
        CommandKind.UpgradeClassIsland => SoftwareUpgradeClassIsland,
        CommandKind.InstallPlugins => PluginInstall,
        CommandKind.UninstallPlugins => PluginUninstall,
        CommandKind.SetPluginEnabled => PluginEnable,
        CommandKind.SetPluginManagementPolicy => PluginManagementPolicy,
        CommandKind.DistributeProfile => ProfileDistribute,
        CommandKind.UpdateTimeLayout => TimeLayoutUpdate,
        CommandKind.JoinManagement => ManagementJoin,
        CommandKind.SetSubjectTeacher => ScheduleSubjectTeacher,
        CommandKind.ExecuteTerminalCommand => TerminalExecute,
        CommandKind.SendFile => FileDistribute,
        CommandKind.ApplyExtensionSettings => ExtensionsSettings,
        CommandKind.ApplyProfile => ProfileApply,
        _ => null,
    };
}

public enum ScheduleChangeMode
{
    Exchange = 1,
    Replace = 2,
}

public enum ScheduleSyncSource
{
    Unknown = 0,
    Plugin = 1,
    WebUi = 2,
    Watch = 3,
    Automatic = 4,
    Connection = 5,
    Mobile = 6,
}

public enum ScheduleSyncTaskState
{
    Running = 1,
    Completed = 2,
    Failed = 3,
    Busy = 4,
}

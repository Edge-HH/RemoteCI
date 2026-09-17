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
    public const string MessageTypeSettingsSync = "settings_sync";
    public const string MessageTypePluginNetworkInfo = "plugin_network_info";
    public const string MessageTypeConnectionBootstrap = "connection_bootstrap";
    public const string MessageTypePeerCapabilities = "peer_capabilities";
    public const string MessageTypeCapabilitiesSync = "capabilities_sync";

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
    All = ViewCurrentCourse | AccessWebUi | ManageUsers | SendNotifications | ManageSchedule |
          PowerControl | TeacherComing | RunExtensions | MainMenuControl | SendVoiceMessages,
}

public static class RolePermissions
{
    /// <summary>可授予普通账号或自定义角色的权限集合。</summary>
    public const UserPermissions Assignable = UserPermissions.AccessWebUi | UserPermissions.ManageUsers |
        UserPermissions.SendNotifications | UserPermissions.ManageSchedule | UserPermissions.PowerControl |
        UserPermissions.TeacherComing | UserPermissions.RunExtensions | UserPermissions.MainMenuControl | UserPermissions.SendVoiceMessages;

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
        CommandKind.ChangeSchedule => UserPermissions.ManageSchedule,
        CommandKind.SendNotification or CommandKind.ClearNotifications => UserPermissions.SendNotifications,
        CommandKind.SendVoiceMessage => UserPermissions.SendVoiceMessages,
        CommandKind.TeacherComing => UserPermissions.TeacherComing,
        CommandKind.SetMainMenuVisibility => UserPermissions.MainMenuControl,
        CommandKind.Power or CommandKind.Volume => UserPermissions.PowerControl,
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
    public static IReadOnlyList<string> Current { get; } = [.. Baseline, VoiceMessageSend];

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

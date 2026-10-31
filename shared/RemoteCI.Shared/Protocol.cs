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
    /// <summary>服务端发给插件的学期设置（第 1 教学周的周一），插件写入 ClassIsland 单周起始日期，使多周轮换全校一致。</summary>
    public const string MessageTypeSemester = "semester";
    /// <summary>插件向服务端报告大文件分发的下载进度与最终结果（file.transfer 能力）。</summary>
    public const string MessageTypeFileTransferProgress = "file_transfer_progress";
    /// <summary>服务端发给插件的应用守护生效策略（全局默认 + 单设备覆盖），插件据此部署或注销守护进程。</summary>
    public const string MessageTypeAppGuardPolicy = "app_guard_policy";
    /// <summary>插件上报守护进程记录的事件（自动恢复、崩溃重启、备份结果等）。</summary>
    public const string MessageTypeAppGuardEvent = "app_guard_event";
    /// <summary>服务端发给插件的调课集合（schedule.adjustments 能力），插件据此生成托管临时层；回执随 schedule_sync 上报。</summary>
    public const string MessageTypeScheduleAdjustments = "schedule_adjustments";

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
/// 班级内的有效权限位：按账号在该班级的角色（或任教老师角色）默认权限，再套用个人调整计算。
/// 管理员的有效权限固定为 All；普通用户至少拥有 ViewCurrentCourse。
/// 第 16 位起是从原有权限中拆出的独立权限；升级时已拥有原权限的角色与账号会自动补齐，行为不变。
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
    // 经服务端文件中转把文件发到有权班级的设备桌面、下载或文档文件夹；只能用服务端上传的文件，不能指定任意路径。
    SendFiles = 1 << 14,
    // 获取有权班级教室电脑的当前屏幕截图；班级管理员默认拥有。
    CaptureScreen = 1 << 15,
    // 本班档案：查看、编辑、下发与收集档案，下发时间表；原先随换课与人员管理授予。
    ManageClassProfile = 1 << 16,
    // 让在线插件立即重新上报本班课表；原先由班级自治策略控制。
    PullSchedule = 1 << 17,
    // 清除教室端通知；原先随发送通知授予。
    ClearNotifications = 1 << 18,
    // 调节音量与静音；原先随电源控制授予。
    VolumeControl = 1 << 19,
    // 上传与获取 ClassIsland 主界面文件；原先随主界面控制授予。
    MainWindowLayout = 1 << 20,
    // 在教室电脑上执行远程终端命令；原先随人员管理授予。
    RemoteTerminal = 1 << 21,
    // 启动、重启 ClassIsland 与加入 ClassIsland 集控；原先随人员管理授予。
    ControlClassIsland = 1 << 22,
    // 修改本班已开放的扩展插件设置；原先仅本班班主任（随扩展功能）可用。
    ConfigureExtensions = 1 << 23,
    // 安装、卸载、启停插件与下发插件包、插件管理策略；原先随人员管理授予。
    ManagePlugins = 1 << 24,
    // 升级 ClassIsland 与插件、刷新版本清单；原先随人员管理授予。
    UpgradeSoftware = 1 << 25,
    // 停用、卸载与更新本班设备上的应用守护；原先随人员管理授予。
    ManageDeviceGuard = 1 << 26,
    // 修改班级名称与头像；原先由班级自治策略控制。
    ManageClassInfo = 1 << 27,
    // 生成与吊销本班电子班牌链接；原先仅本班班主任可用。
    ManageClassBoards = 1 << 28,
    // 查看本班操作记录；原先仅本班班主任可用。
    ViewAuditLog = 1 << 29,
    // 查看本班课时统计；原先仅本班班主任可用。
    ViewLessonStats = 1 << 30,
    All = ViewCurrentCourse | AccessWebUi | ManageUsers | SendNotifications | ManageSchedule |
          PowerControl | TeacherComing | RunExtensions | MainMenuControl | SendVoiceMessages |
          ChangeDisplayName | ApiAccess | RequestScheduleSwap | ForceScheduleSwap | SendFiles | CaptureScreen |
          ManageClassProfile | PullSchedule | ClearNotifications | VolumeControl | MainWindowLayout |
          RemoteTerminal | ControlClassIsland | ConfigureExtensions | ManagePlugins | UpgradeSoftware |
          ManageDeviceGuard | ManageClassInfo | ManageClassBoards | ViewAuditLog | ViewLessonStats,
}

/// <summary>
/// 系统管理权限：面向全部班级的管理功能，只按账号的全局角色与个人调整计算，不随班内角色变化。
/// 管理员固定拥有全部；系统配置、更新与备份，以及管理员账号本身仍只由系统管理员维护。
/// </summary>
[Flags]
public enum ManagementPermissions
{
    None = 0,
    // 创建、编辑、删除角色及其默认权限。
    ManageRoles = 1 << 0,
    // Excel 与文本批量导入、导出人员。
    ImportUsers = 1 << 1,
    // 班级管理：建班、分组、成员、连接码、设备分配与访客开关。
    ManageClasses = 1 << 2,
    // 档案库：全部班级与模板档案的编辑、下发、收集与版本历史。
    ManageProfileLibrary = 1 << 3,
    // 学期与调休。
    ManageHolidays = 1 << 4,
    // 设备状态：全部班级的在线、版本与告警。
    ViewDevices = 1 << 5,
    // 批量控制：向多个班级下发命令；每项操作仍需对应的班级权限。
    BatchControl = 1 << 6,
    // 自动化规则。
    ManageAutomation = 1 << 7,
    // 应用守护的全局与单设备策略。
    ManageAppGuard = 1 << 8,
    // 插件批量下发、扩展调用策略与插件的班级开放设置。
    ManageExtensionSettings = 1 << 9,
    // 登录页外观与访客访问。
    ManageLoginPage = 1 << 10,
    // 插件凭据、连接码与课表同步设置。
    ManagePluginAccess = 1 << 11,
    // 查看全部班级的操作记录。
    ViewAllAuditLogs = 1 << 12,
    // 查看全部班级的课时统计。
    ViewAllLessonStats = 1 << 13,
    All = ManageRoles | ImportUsers | ManageClasses | ManageProfileLibrary | ManageHolidays | ViewDevices |
          BatchControl | ManageAutomation | ManageAppGuard | ManageExtensionSettings | ManageLoginPage |
          ManagePluginAccess | ViewAllAuditLogs | ViewAllLessonStats,
}

public static class RolePermissions
{
    /// <summary>可授予普通账号或自定义角色的权限集合。</summary>
    public const UserPermissions Assignable = UserPermissions.All &
        ~UserPermissions.ViewCurrentCourse & ~UserPermissions.ChangeDisplayName;

    /// <summary>
    /// 有效权限：角色默认权限加上个人新增、减去个人收回。个人设置记录的是相对角色的调整，
    /// 因此同一套调整在每个班级都按该班的班内角色生效，角色默认权限变化时未调整的权限随之变化。
    /// </summary>
    public static UserPermissions Effective(
        UserRole role,
        UserPermissions granted,
        UserPermissions roleDefaults = UserPermissions.None,
        UserPermissions revoked = UserPermissions.None) => role == UserRole.Admin
        ? UserPermissions.All
        : UserPermissions.ViewCurrentCourse |
          (((roleDefaults | granted) & ~revoked) & ~UserPermissions.ViewCurrentCourse);

    /// <summary>系统管理权限的有效值，规则与班级权限相同：角色默认 + 个人新增 − 个人收回。</summary>
    public static ManagementPermissions EffectiveManagement(
        UserRole role,
        ManagementPermissions roleDefaults,
        ManagementPermissions granted = ManagementPermissions.None,
        ManagementPermissions revoked = ManagementPermissions.None) => role == UserRole.Admin
        ? ManagementPermissions.All
        : (roleDefaults | granted) & ~revoked & ManagementPermissions.All;

    /// <summary>
    /// 把编辑器里勾选的完整权限集合换算成相对角色默认的个人调整：
    /// 勾选但角色没有的记为新增，角色有但未勾选的记为收回。
    /// </summary>
    public static (UserPermissions Granted, UserPermissions Revoked) Adjust(
        UserPermissions roleDefaults, UserPermissions selected)
    {
        roleDefaults &= Assignable;
        selected &= Assignable;
        return (selected & ~roleDefaults, roleDefaults & ~selected);
    }

    /// <inheritdoc cref="Adjust(UserPermissions, UserPermissions)"/>
    public static (ManagementPermissions Granted, ManagementPermissions Revoked) Adjust(
        ManagementPermissions roleDefaults, ManagementPermissions selected)
    {
        roleDefaults &= ManagementPermissions.All;
        selected &= ManagementPermissions.All;
        return (selected & ~roleDefaults, roleDefaults & ~selected);
    }

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
    /// <summary>读取设备当前档案 JSON，供服务端档案管理收集后编辑；仅档案管理入口可以发起。</summary>
    ReadProfile = 26,
    /// <summary>截取设备当前屏幕并上传到服务端；仅服务端截图入口可以发起（需先登记上传凭据）。</summary>
    CaptureScreen = 27,
    /// <summary>由设备外守护代理启动 ClassIsland；课表已经运行时幂等成功。</summary>
    LaunchClassIsland = 28,
    /// <summary>上传 ClassIsland 主界面文件（组件配置），覆盖当前文件或新建并可立即使用。</summary>
    ApplyMainWindowLayout = 29,
    /// <summary>读取设备当前使用的 ClassIsland 主界面文件（组件配置）。</summary>
    ReadMainWindowLayout = 30,
    /// <summary>从服务端文件中转下载一组 .cipx 插件包，放入 ClassIsland 插件包缓存并重启安装（安装或更新）。</summary>
    InstallPluginPackages = 31,
    /// <summary>停用设备外守护并退出，保留安装文件以便之后重新启用。</summary>
    StopAppGuard = 32,
    /// <summary>停用并卸载设备外守护，移除增强服务、登录任务、登录自启与守护文件。</summary>
    UninstallAppGuard = 33,
    /// <summary>由课表插件立即用插件包随附的守护程序覆盖本机守护并重新启动，失败时返回原因。</summary>
    UpdateAppGuard = 34,
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
    /// <summary>
    /// 只能由服务端专用管理入口构造并定向发送的命令：服务端要按班级自治策略、档案修订号等复核，
    /// 通用 REST 命令接口、手机/手表命令通道和插件局域网直连一律拒绝。各通道共用这一份清单。
    /// </summary>
    public static bool IsServerOnly(CommandKind command) =>
        command is CommandKind.ApplyExtensionSettings or CommandKind.ApplyProfile or CommandKind.ReadProfile or
            CommandKind.CaptureScreen or CommandKind.InstallPluginPackages or CommandKind.StopAppGuard or CommandKind.UninstallAppGuard or
            CommandKind.UpdateAppGuard;

    public static UserPermissions Required(CommandKind command) => command switch
    {
        CommandKind.ChangeSchedule or CommandKind.SetSubjectTeacher => UserPermissions.ManageSchedule,
        CommandKind.ApplyProfile or CommandKind.ReadProfile or CommandKind.DistributeProfile or CommandKind.UpdateTimeLayout
            => UserPermissions.ManageClassProfile,
        CommandKind.SendNotification => UserPermissions.SendNotifications,
        CommandKind.ClearNotifications => UserPermissions.ClearNotifications,
        CommandKind.SendVoiceMessage => UserPermissions.SendVoiceMessages,
        CommandKind.TeacherComing => UserPermissions.TeacherComing,
        CommandKind.SetMainMenuVisibility => UserPermissions.MainMenuControl,
        CommandKind.ApplyMainWindowLayout or CommandKind.ReadMainWindowLayout => UserPermissions.MainWindowLayout,
        CommandKind.Power => UserPermissions.PowerControl,
        CommandKind.Volume => UserPermissions.VolumeControl,
        // 服务端另按插件的班级开放策略复核，插件端只校验权限位作为纵深防御。
        CommandKind.ApplyExtensionSettings => UserPermissions.ConfigureExtensions,
        // 文件分发只写入固定的用户文件夹；内联文件（旧方式）另外仅限系统管理员，由通用命令通道与插件分别复核。
        CommandKind.SendFile => UserPermissions.SendFiles,
        CommandKind.CaptureScreen => UserPermissions.CaptureScreen,
        CommandKind.UpgradePlugins or CommandKind.UpgradeClassIsland or CommandKind.RefreshSoftwareInventory
            => UserPermissions.UpgradeSoftware,
        CommandKind.InstallPlugins or CommandKind.UninstallPlugins or CommandKind.SetPluginEnabled or
        CommandKind.InstallPluginPackages or CommandKind.SetPluginManagementPolicy
            => UserPermissions.ManagePlugins,
        CommandKind.RestartClassIsland or CommandKind.LaunchClassIsland or CommandKind.JoinManagement
            => UserPermissions.ControlClassIsland,
        CommandKind.StopAppGuard or CommandKind.UninstallAppGuard or CommandKind.UpdateAppGuard
            => UserPermissions.ManageDeviceGuard,
        CommandKind.ExecuteTerminalCommand => UserPermissions.RemoteTerminal,
        _ => UserPermissions.None,
    };

    /// <summary>
    /// 权限细化前（3.4 之前）插件对同一命令校验的权限位。旧插件只认这些位，
    /// 服务端代发命令时据此补上，避免已获授权的新权限在未升级的教室端被误拒。
    /// </summary>
    public static UserPermissions LegacyRequired(CommandKind command) => command switch
    {
        CommandKind.ApplyProfile or CommandKind.ReadProfile => UserPermissions.ManageSchedule,
        CommandKind.ClearNotifications => UserPermissions.SendNotifications,
        CommandKind.ApplyMainWindowLayout or CommandKind.ReadMainWindowLayout => UserPermissions.MainMenuControl,
        CommandKind.Volume => UserPermissions.PowerControl,
        CommandKind.ApplyExtensionSettings => UserPermissions.RunExtensions,
        CommandKind.DistributeProfile or CommandKind.UpdateTimeLayout or
        CommandKind.UpgradePlugins or CommandKind.UpgradeClassIsland or CommandKind.RefreshSoftwareInventory or
        CommandKind.InstallPlugins or CommandKind.UninstallPlugins or CommandKind.SetPluginEnabled or
        CommandKind.InstallPluginPackages or CommandKind.SetPluginManagementPolicy or
        CommandKind.RestartClassIsland or CommandKind.LaunchClassIsland or CommandKind.JoinManagement or
        CommandKind.StopAppGuard or CommandKind.UninstallAppGuard or CommandKind.UpdateAppGuard or
        CommandKind.ExecuteTerminalCommand => UserPermissions.ManageUsers,
        _ => Required(command),
    };

    /// <summary>
    /// 服务端把命令发往插件前调用：发起人已具备该命令的新权限时，在这一条命令携带的权限里补上旧插件认的权限位。
    /// 只影响这一条命令消息，服务端鉴权始终以 <see cref="Required"/> 为准。
    /// </summary>
    public static UserPermissions ForDispatch(CommandKind command, UserPermissions permissions)
    {
        var required = Required(command);
        return required != UserPermissions.None && permissions.HasFlag(required)
            ? permissions | LegacyRequired(command)
            : permissions;
    }
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
    /// <summary>按传输编号从服务端分段下载文件（断点续传、SHA-256 校验），并上报进度；不再受内联 10 MB 限制。</summary>
    public const string FileTransfer = "file.transfer";
    /// <summary>同步扩展分组与设置页，并接受远程修改扩展设置。</summary>
    public const string ExtensionsSettings = "extensions.settings";
    /// <summary>按明确选择的方式更新、替换或创建并启用服务端档案。</summary>
    public const string ProfileApply = "profile.apply";
    /// <summary>读取设备当前档案，供服务端档案管理收集。</summary>
    public const string ProfileRead = "profile.read";
    /// <summary>按日期把服务端档案中的临时层写入设备（ProfileApplyMode.TempLayers）。</summary>
    public const string ProfileTempLayer = "profile.temp-layer";
    /// <summary>接收调休日历并在放假日关闭课表、调休上学日建立临时课表。</summary>
    public const string HolidayCalendar = "schedule.holiday-calendar";
    /// <summary>接收学期设置并写入 ClassIsland 的单周起始日期（清除本机轮换偏移）。</summary>
    public const string Semester = "schedule.semester";
    /// <summary>接收应用守护策略：退出自启、意外退出重启、防删除备份与恢复。</summary>
    public const string AppGuard = "host.app-guard";
    /// <summary>接收服务端调课集合，按“基础课表 + 调课记录”生成并维护托管临时层，在课表中回报结果与本地调课。</summary>
    public const string ScheduleAdjustments = "schedule.adjustments";
    /// <summary>截取当前屏幕并凭一次性上传凭据上传到服务端。</summary>
    public const string ScreenCapture = "screen.capture";
    /// <summary>课表离线时由设备外守护代理启动 ClassIsland。</summary>
    public const string AppGuardLaunch = "host.app-guard-launch";
    /// <summary>由设备外守护代理执行关闭或完整卸载。</summary>
    public const string AppGuardManage = "host.app-guard-manage";
    /// <summary>上传 ClassIsland 主界面文件：覆盖当前文件，或新建并可立即使用。</summary>
    public const string MainWindowLayoutApply = "main-window.layout-apply";
    /// <summary>读取设备当前使用的 ClassIsland 主界面文件。</summary>
    public const string MainWindowLayoutRead = "main-window.layout-read";
    /// <summary>从服务端文件中转接收 .cipx 插件包并安装或更新（服务端预下载与上传 CIPX 共用）。</summary>
    public const string PluginPackageInstall = "plugin.package-install";
    /// <summary>课表插件立即用随附版本覆盖并重启本机守护。</summary>
    public const string AppGuardUpdate = "host.app-guard-update";

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
            ScheduleSubjectTeacher, TerminalExecute, FileDistribute, ExtensionsSettings, ProfileApply, HolidayCalendar,
            ProfileRead, ProfileTempLayer, Semester, FileTransfer, AppGuard, ScheduleChangePermanent, ScheduleAdjustments,
            ScreenCapture, AppGuardLaunch, AppGuardManage, MainWindowLayoutApply, MainWindowLayoutRead, PluginPackageInstall,
            AppGuardUpdate];

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
        FileTransfer => "大文件分发（断点续传）",
        ExtensionsSettings => "修改扩展设置",
        ProfileApply => "应用服务端档案",
        ProfileRead => "读取设备档案",
        ProfileTempLayer => "下发临时层",
        HolidayCalendar => "调休自动适配",
        Semester => "同步学期开始日期",
        AppGuard => "应用守护",
        ScheduleChangePermanent => "永久换课",
        ScheduleAdjustments => "按调课记录生成临时层",
        ScreenCapture => "屏幕截图",
        AppGuardLaunch => "守护代理远程打开 ClassIsland",
        AppGuardManage => "守护代理关闭与卸载",
        MainWindowLayoutApply => "上传主界面文件",
        MainWindowLayoutRead => "获取主界面文件",
        PluginPackageInstall => "从服务端安装插件包",
        AppGuardUpdate => "立即更新守护",
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
        CommandKind.ReadProfile => ProfileRead,
        CommandKind.CaptureScreen => ScreenCapture,
        CommandKind.LaunchClassIsland => AppGuardLaunch,
        CommandKind.StopAppGuard or CommandKind.UninstallAppGuard => AppGuardManage,
        CommandKind.ApplyMainWindowLayout => MainWindowLayoutApply,
        CommandKind.ReadMainWindowLayout => MainWindowLayoutRead,
        CommandKind.InstallPluginPackages => PluginPackageInstall,
        CommandKind.UpdateAppGuard => AppGuardUpdate,
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

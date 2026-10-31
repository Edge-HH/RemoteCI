using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>编辑器中的一项权限：班级权限或系统管理权限二选一。</summary>
public sealed record PermissionItem(
    string Label,
    string Hint,
    UserPermissions Permission = UserPermissions.None,
    ManagementPermissions Management = ManagementPermissions.None)
{
    public bool IsManagement => Management != ManagementPermissions.None;

    /// <summary>基础权限（查看课表与当前课程）：所有账号始终拥有，编辑器中只读。</summary>
    public bool IsBase => Permission == UserPermissions.ViewCurrentCourse;

    public int Value => IsManagement ? (int)Management : (int)Permission;
}

/// <summary>编辑器中的一组权限；<see cref="IconSvg"/> 是 24×24 描边图标的内部图形。</summary>
public sealed record PermissionGroup(string Id, string Title, string Scope, string IconSvg, IReadOnlyList<PermissionItem> Items);

/// <summary>
/// 角色与人员编辑器展示的完整权限目录：每个可授予的权限都在这里出现一次，按功能分组。
/// “班级内”分组按账号在各班级的角色计算；“全局”分组按账号的全局角色计算，对全部班级生效。
/// </summary>
public static class PermissionCatalog
{
    public const string ClassScope = "班级内";
    public const string GlobalScope = "全局";

    /// <summary>不在编辑器中出现、保存时按原值保留的权限位（“老师来了”暂不在 WebUI 暴露）。</summary>
    public const UserPermissions Hidden = UserPermissions.TeacherComing;

    public static IReadOnlyList<PermissionGroup> Groups { get; } =
    [
        new("schedule", "课表与换课", ClassScope,
            """<path d="M8 2v4m8-4v4"/><rect width="18" height="18" x="3" y="4" rx="2"/><path d="M3 10h18M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01"/>""",
            [
                new("查看课表与当前课程", "所有账号的基础权限，始终拥有", UserPermissions.ViewCurrentCourse),
                new("直接换课与编辑课表", "在课表页、手机与手表上直接换课，修改科目任课老师，删除直接换课的调课记录", UserPermissions.ManageSchedule),
                new("发起换课申请", "向对方老师发起临时换课，对方审批后生效", UserPermissions.RequestScheduleSwap),
                new("强制换课", "不经审批立即换课，对方老师可撤回", UserPermissions.ForceScheduleSwap),
                new("从教室刷新课表", "让在线插件立即重新上报本班课表", UserPermissions.PullSchedule),
                new("本班档案", "查看、编辑、下发与收集本班 ClassIsland 档案和时间表", UserPermissions.ManageClassProfile),
            ]),
        new("messages", "通知与消息", ClassScope,
            """<path d="M10.268 21a2 2 0 0 0 3.464 0M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/>""",
            [
                new("发送通知", "向教室电脑发送文字通知", UserPermissions.SendNotifications),
                new("清除通知", "清除教室电脑上正在显示的通知", UserPermissions.ClearNotifications),
                new("语音消息", "录制并在教室电脑上播放语音", UserPermissions.SendVoiceMessages),
            ]),
        new("device", "教室电脑", ClassScope,
            """<rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8m-4-4v4"/>""",
            [
                new("关机、重启与睡眠", "远程控制教室电脑电源", UserPermissions.PowerControl),
                new("音量与静音", "调节教室电脑音量或静音", UserPermissions.VolumeControl),
                new("显示或隐藏主界面", "切换 ClassIsland 主界面的显示状态", UserPermissions.MainMenuControl),
                new("主界面文件", "上传或获取 ClassIsland 主界面（组件）配置文件", UserPermissions.MainWindowLayout),
                new("屏幕截图", "获取教室电脑当前屏幕", UserPermissions.CaptureScreen),
                new("文件分发", "经服务端中转把文件发到教室电脑的桌面、下载或文档文件夹", UserPermissions.SendFiles),
                new("远程终端", "在教室电脑上执行命令，属于高风险操作", UserPermissions.RemoteTerminal),
            ]),
        new("classisland", "ClassIsland 与插件", ClassScope,
            """<path d="m7.5 4.27 9 5.15"/><path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"/><path d="m3.3 7 8.7 5 8.7-5M12 22V12"/>""",
            [
                new("使用扩展功能", "运行其他插件接入的远程功能（还受各扩展的开放设置限制）", UserPermissions.RunExtensions),
                new("修改本班插件设置", "修改已开放给班级的扩展插件设置", UserPermissions.ConfigureExtensions),
                new("管理插件", "安装、卸载、启用或停用插件，下发插件包与插件管理策略", UserPermissions.ManagePlugins),
                new("升级软件", "升级 ClassIsland 与插件，刷新版本清单", UserPermissions.UpgradeSoftware),
                new("ClassIsland 程序控制", "远程启动、重启 ClassIsland，加入 ClassIsland 集控", UserPermissions.ControlClassIsland),
                new("设备应用守护", "停用、卸载或立即更新教室电脑上的应用守护", UserPermissions.ManageDeviceGuard),
            ]),
        new("class", "班级", ClassScope,
            """<path d="M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z"/><path d="M22 10v6M6 12.5V16a6 3 0 0 0 12 0v-3.5"/>""",
            [
                new("班级名称与头像", "修改所在班级的班名，上传或清除班级头像", UserPermissions.ManageClassInfo),
                new("电子班牌", "生成与吊销本班电子班牌链接", UserPermissions.ManageClassBoards),
                new("本班操作记录与设备状态", "查看本班的命令操作记录和设备在线状态", UserPermissions.ViewAuditLog),
                new("本班课时统计", "查看与导出本班课时与代课统计", UserPermissions.ViewLessonStats),
            ]),
        new("account", "人员与账号", GlobalScope,
            """<circle cx="9" cy="7" r="4"/><path d="M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2M16 3.13a4 4 0 0 1 0 7.75M21 21v-2a4 4 0 0 0-3-3.85"/>""",
            [
                new("概览页", "打开 WebUI 概览仪表盘与连接状态", UserPermissions.AccessWebUi),
                new("API 访问", "生成并使用 API Key 调用 REST 接口，按账号当前权限逐次鉴权", UserPermissions.ApiAccess),
                new("人员管理", "新建、编辑、停用、删除账号，重置密码，管理 API Key", UserPermissions.ManageUsers),
                new("角色配置", "创建、编辑、删除角色及其默认权限", Management: ManagementPermissions.ManageRoles),
                new("批量导入导出人员", "Excel 与文本批量导入、导出人员与成员关系", Management: ManagementPermissions.ImportUsers),
            ]),
        new("academic", "教务与班级管理", GlobalScope,
            """<path d="M3 21h18M5 21V8l7-5 7 5v13"/><path d="M9 21v-5h6v5M9 11h.01M15 11h.01"/>""",
            [
                new("班级管理", "建班、分组、成员、连接码、设备分配与访客开关", Management: ManagementPermissions.ManageClasses),
                new("档案库", "管理模板与全部班级的档案、下发、收集与版本历史", Management: ManagementPermissions.ManageProfileLibrary),
                new("学期与调休", "设置学期、教学周与节假日调休", Management: ManagementPermissions.ManageHolidays),
                new("查看全部操作记录", "查看全部班级与未分配设备的操作记录", Management: ManagementPermissions.ViewAllAuditLogs),
                new("查看全部课时统计", "查看与导出全部班级的课时统计", Management: ManagementPermissions.ViewAllLessonStats),
            ]),
        new("system", "设备与系统", GlobalScope,
            """<rect width="20" height="8" x="2" y="2" rx="2"/><rect width="20" height="8" x="2" y="14" rx="2"/><path d="M6 6h.01M6 18h.01"/>""",
            [
                new("设备状态", "查看全部班级设备的在线、版本与告警", Management: ManagementPermissions.ViewDevices),
                new("批量控制", "向多个班级下发命令；每项操作仍需上面对应的权限", Management: ManagementPermissions.BatchControl),
                new("自动化", "创建与运行自动化规则；规则以修改人的权限执行", Management: ManagementPermissions.ManageAutomation),
                new("应用守护策略", "设置全局与单设备的应用守护策略", Management: ManagementPermissions.ManageAppGuard),
                new("插件批量下发", "跨班级下发插件设置，管理扩展调用策略与插件的班级开放", Management: ManagementPermissions.ManageExtensionSettings),
                new("登录页与访客", "登录页外观、扫码登录地址与访客自动进入", Management: ManagementPermissions.ManageLoginPage),
                new("插件凭据与连接码", "生成配对码，吊销插件凭据，设置课表自动拉取间隔", Management: ManagementPermissions.ManagePluginAccess),
            ]),
    ];

    /// <summary>仍只有系统管理员可用、不能通过权限授予的功能，在编辑器底部说明。</summary>
    public const string AdminOnlyNote = "系统配置、服务端更新与备份，以及管理员账号本身，仍只有系统管理员可以操作。";

    /// <summary>目录中出现的全部班级权限位（不含基础权限与隐藏位），用于校验目录完整。</summary>
    public static UserPermissions ListedPermissions => Groups.SelectMany(x => x.Items)
        .Where(x => !x.IsManagement && !x.IsBase)
        .Aggregate(UserPermissions.None, (all, x) => all | x.Permission);

    public static ManagementPermissions ListedManagement => Groups.SelectMany(x => x.Items)
        .Aggregate(ManagementPermissions.None, (all, x) => all | x.Management);

    /// <summary>编辑器表单提交的勾选值换算成权限集合（只保留可授予的位）。</summary>
    public static UserPermissions ToPermissions(IEnumerable<int>? values) =>
        (values ?? []).Aggregate(UserPermissions.None, (all, x) => all | (UserPermissions)x) & RolePermissions.Assignable;

    public static ManagementPermissions ToManagement(IEnumerable<int>? values) =>
        (values ?? []).Aggregate(ManagementPermissions.None, (all, x) => all | (ManagementPermissions)x) & ManagementPermissions.All;
}

/// <summary>
/// 分组权限清单（_PermissionChecklist）的数据：勾选状态即账号或角色最终拥有的权限。
/// 人员编辑器传入所选角色的默认权限作为基准，与基准不同的项标为“已新增 / 已收回”，可一键恢复为角色默认。
/// </summary>
public sealed record PermissionChecklistView(
    string Prefix,
    UserPermissions Selected,
    ManagementPermissions SelectedManagement,
    string Title,
    string Description,
    UserPermissions Baseline = UserPermissions.None,
    ManagementPermissions BaselineManagement = ManagementPermissions.None,
    bool ShowBaseline = false,
    bool Locked = false)
{
    public bool IsChecked(PermissionItem item) => Locked || item.IsBase ||
        (item.IsManagement ? SelectedManagement.HasFlag(item.Management) : Selected.HasFlag(item.Permission));

    public bool InBaseline(PermissionItem item) => item.IsBase ||
        (item.IsManagement ? BaselineManagement.HasFlag(item.Management) : Baseline.HasFlag(item.Permission));

    /// <summary>与角色默认不同的项："已新增" 或 "已收回"；没有基准或一致时为 null。</summary>
    public string? AdjustmentLabel(PermissionItem item) =>
        !ShowBaseline || Locked || item.IsBase || IsChecked(item) == InBaseline(item)
            ? null
            : IsChecked(item) ? "已新增" : "已收回";
}

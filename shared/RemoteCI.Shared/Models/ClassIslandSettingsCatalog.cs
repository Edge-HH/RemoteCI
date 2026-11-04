using System.Text.Json;

namespace RemoteCI.Shared.Models;

/// <summary>ClassIsland 设置项在远程表单中的值类型。</summary>
public enum ClassIslandSettingType
{
    Boolean = 1,
    Integer = 2,
    Double = 3,
    String = 4,
    /// <summary>整型枚举：值为 ClassIsland 设置页下拉框的下标。</summary>
    Enum = 5,
    /// <summary>颜色：#RRGGBB 或 #AARRGGBB 十六进制字符串。</summary>
    Color = 6,
    /// <summary>日期：yyyy-MM-dd 字符串（设备端按本地日期处理）。</summary>
    Date = 7,
    /// <summary>字符串列表：JSON 字符串数组（如全屏排除列表、排除的气象预警）。</summary>
    StringList = 8,
}

/// <summary>整型枚举设置项的单个选项：值为写回 ClassIsland 的下标，标签与 ClassIsland 设置页一致。</summary>
public sealed record ClassIslandSettingOption(int Value, string Label);

/// <summary>单个 ClassIsland 设置项的描述：键、类型、界面文案、所属设置页与控件参数。</summary>
public sealed class ClassIslandSettingDescriptor
{
    /// <summary>ClassIsland <c>Settings</c> 的属性名（即 Settings.json 的 JSON 键）。</summary>
    public required string Key { get; init; }
    public required ClassIslandSettingType Type { get; init; }
    /// <summary>与 ClassIsland 设置页一致的条目名称。</summary>
    public required string Label { get; init; }
    public string? Description { get; init; }
    /// <summary>所属设置页 Id，见 <see cref="ClassIslandSettingsCatalog.Pages"/>。</summary>
    public required string Page { get; init; }
    /// <summary>设置页内的卡片（分区）标题；同页同卡片的条目渲染在一张卡片里。</summary>
    public string? Card { get; init; }
    /// <summary>枚举选项；仅 <see cref="ClassIslandSettingType.Enum"/> 使用。</summary>
    public IReadOnlyList<ClassIslandSettingOption>? Options { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    /// <summary>数值单位（秒、天等），只用于显示。</summary>
    public string? Unit { get; init; }
    /// <summary>调试等高危设置：默认折叠并提示谨慎修改。</summary>
    public bool Advanced { get; init; }
}

/// <summary>远程设置表单中的一个设置页（对应 ClassIsland 设置窗口左侧导航的一项）。</summary>
public sealed record ClassIslandSettingsPage(string Id, string Name, int Order);

/// <summary>
/// ClassIsland 设置项目录：服务端 WebUI 表单与插件端读写的共同白名单。
/// 键名与 ClassIsland 2.1 的 <c>ClassIsland.Models.Settings</c> 属性一一对应，
/// 名称、枚举选项与控件范围取自 ClassIsland 设置页（Views/SettingPages）。
/// 宿主有副作用或由其他机制托管的项（开机自启、课表档案、组件方案内容、轮换偏移等）不进目录。
/// </summary>
public static class ClassIslandSettingsCatalog
{
    /// <summary>一次远程设置修改允许携带的最大项数。</summary>
    public const int MaxPatchValues = ClassIslandSettingsRequest.MaxValues;

    public static IReadOnlyList<ClassIslandSettingsPage> Pages { get; } =
    [
        new("general", "基本", 1),
        new("appearance", "外观", 2),
        new("window", "窗口", 3),
        new("notification", "提醒", 4),
        new("weather", "天气", 5),
        new("update", "更新", 6),
        new("automation", "自动化", 7),
        new("storage", "存储", 8),
        new("privacy", "隐私", 9),
        new("plugins", "插件", 10),
        new("components", "主界面", 11),
        new("debug", "调试", 12),
    ];

    public static IReadOnlyList<ClassIslandSettingDescriptor> Items { get; } =
    [
        // ---------- 基本 ----------
        Date("SingleWeekStartTime", "学期开始时间", "学期首日，作为多周轮换课表与单双周的计算起点。", "general", "学期"),
        Integer("MultiWeekRotationMaxCycle", "最大多周轮换周数", "多周轮换课表的最大循环周数。", "general", "学期", min: 2, max: 9, unit: "周"),
        Enum("TaskBarIconClickBehavior", "点击托盘图标行为", "鼠标左键点击托盘图标时进行的操作。", "general", "行为",
            [new(0, "打开主菜单"), new(1, "打开档案编辑窗口"), new(2, "显示/隐藏主界面"), new(3, "打开换课窗口"), new(4, "打开设置窗口")]),
        Boolean("ShowExtraInfoOnTimePoint", "时间点上显示附加信息", "在时间点上显示倒计时等附加信息。", "general", "行为"),
        Boolean("IsCountdownEnabled", "启用倒计时", "时间点上显示活动倒计时。", "general", "行为"),
        Integer("CountdownSeconds", "倒计时提醒秒数", "活动开始前多少秒开始显示倒计时。", "general", "行为", min: 1, max: 3600, unit: "秒"),
        Boolean("IsClassPrepareNotificationEnabled", "启用课前准备提醒", "下课前按设定时间提醒师生做好课前准备。", "general", "课堂与提醒"),
        Integer("ClassPrepareNotifySeconds", "课前准备提醒提前秒数", null, "general", "课堂与提醒", min: 0, max: 3600, unit: "秒"),
        Boolean("IsClassChangingNotificationEnabled", "启用上课提醒", "开始上课时在教室端显示提醒。", "general", "课堂与提醒"),
        Boolean("IsClassOffNotificationEnabled", "启用下课提醒", "下课时在教室端显示提醒。", "general", "课堂与提醒"),
        Boolean("ShowCurrentLessonOnlyOnClass", "上课时仅显示当前课程", null, "general", "课堂与提醒"),
        Integer("DefaultOnClassTimePointMinutes", "默认上课时长", "新建上课时间点时的默认时长。", "general", "课堂与提醒", min: 1, max: 240, unit: "分钟"),
        Integer("DefaultBreakingTimePointMinutes", "默认课间时长", "新建课间时间点时的默认时长。", "general", "课堂与提醒", min: 0, max: 240, unit: "分钟"),
        Enum("HideMode", "隐藏窗口", "主界面自动隐藏模式；高级模式按隐藏规则集判断。", "general", "隐藏",
            [new(0, "基础模式"), new(1, "高级模式")]),
        Boolean("HideOnClass", "上课时隐藏", null, "general", "隐藏"),
        Boolean("HideOnMaxWindow", "前台是最大化窗口时隐藏", null, "general", "隐藏"),
        Boolean("HideOnFullscreen", "前台是全屏窗口时隐藏", null, "general", "隐藏"),
        StringList("ExcludedFullscreenWindow", "全屏窗口排除列表", "这些进程的全屏窗口不触发自动隐藏。", "general", "隐藏"),
        Boolean("IsExactTimeEnabled", "启用精确时间", "从 NTP 服务器校准教室端时钟。", "general", "精确时间"),
        String("ExactTimeServer", "NTP 服务器", null, "general", "精确时间"),
        Double("TimeOffsetSeconds", "时间偏移", "在精确时间基础上再叠加的偏移秒数。", "general", "精确时间", min: -86400, max: 86400, unit: "秒"),
        Boolean("IsTimeAutoAdjustEnabled", "启用自动校时", "按固定间隔自动重新校时。", "general", "精确时间"),
        Double("TimeAutoAdjustSeconds", "自动校时间隔", null, "general", "精确时间", min: 1, max: 86400, unit: "秒"),
        Boolean("IsSplashEnabled", "显示启动加载界面", "启动时显示加载界面。", "general", "启动"),
        String("SplashCustomText", "启动界面自定义文本", null, "general", "启动"),
        Boolean("ShowDetailedStatusOnSplash", "启动界面显示详细状态", null, "general", "启动"),
        Enum("AnimationLevel", "动画级别", null, "general", "性能与安全",
            [new(0, "禁用"), new(1, "标准"), new(2, "华丽")]),
        Boolean("IsWaitForTransientDisabled", "禁用动画等待", "启动时不等待过渡动画完成，加快启动速度。", "general", "性能与安全"),
        Boolean("ReduceProgressAccuracy", "降低主界面进度条精度", "降低课程进度条的刷新频率以节省资源。", "general", "性能与安全"),
        Boolean("IsCriticalSafeMode", "教学安全模式", "ClassIsland 意外崩溃时按所选方法自动处理，保证教学不中断。", "general", "性能与安全"),
        Enum("CriticalSafeModeMethod", "崩溃处理方法", null, "general", "性能与安全",
            [new(0, "退出应用"), new(1, "静默重启应用"), new(2, "显示通知"), new(3, "忽略（不推荐）")]),
        Boolean("AutoDisableCorruptPlugins", "自动禁用损坏的插件", null, "general", "性能与安全"),

        // ---------- 外观 ----------
        Double("Scale", "界面缩放", null, "appearance", "布局与背景", min: 0.75, max: 2.5, step: 0.05),
        Double("Opacity", "背景不透明度", null, "appearance", "布局与背景", min: 0, max: 1, step: 0.01),
        Double("RadiusX", "圆角半径", null, "appearance", "布局与背景", min: 0, max: 20, unit: "px"),
        Double("MainWindowLineVerticalMargin", "行间隔", "课程行之间的垂直间距。", "appearance", "布局与背景", min: 0, max: 150, unit: "px"),
        Boolean("IsIslandSeperated", "分体主界面", "把时间组件与课程表拆分为两个独立窗口显示。", "appearance", "布局与背景"),
        Boolean("IsCustomBackgroundColorEnabled", "启用自定义背景颜色", null, "appearance", "布局与背景"),
        Color("BackgroundColor", "背景颜色", null, "appearance", "布局与背景"),
        Enum("Theme", "应用主题", "控制应用的明暗主题。", "appearance", "主题与颜色",
            [new(0, "跟随系统"), new(1, "明亮"), new(2, "黑暗")]),
        Enum("ColorSource", "主题色来源", "控制应用选取主题色的途径。", "appearance", "主题与颜色",
            [new(0, "自定义"), new(1, "系统壁纸"), new(2, "系统"), new(3, "屏幕主题色")]),
        Color("PrimaryColor", "强调色", null, "appearance", "主题与颜色"),
        Color("SecondaryColor", "次要颜色", null, "appearance", "主题与颜色"),
        Boolean("IsWallpaperAutoUpdateEnabled", "启用自动提取主题色", "定期从系统壁纸提取主题色。", "appearance", "主题与颜色"),
        Integer("WallpaperAutoUpdateIntervalSeconds", "自动提取主题色间隔", null, "appearance", "主题与颜色", min: 5, max: 3600, unit: "秒"),
        Boolean("IsFallbackModeEnabled", "启用兼容模式", "取色失败时回退到默认主题色。", "appearance", "主题与颜色"),
        Boolean("UseExperimentColorPickingMethod", "实验性取色算法", null, "appearance", "主题与颜色"),
        String("WallpaperClassName", "壁纸层窗口类名", null, "appearance", "主题与颜色"),
        Double("TargetLightValue", "取色亮度波峰", null, "appearance", "主题与颜色", min: 0, max: 1, step: 0.01),
        String("MainWindowFont", "自定义字体", null, "appearance", "字体"),
        Integer("MainWindowFontWeight2", "自定义字重", null, "appearance", "字体", min: 100, max: 900, step: 100),
        Double("MainWindowSecondaryFontSize", "次要字号", null, "appearance", "字体", min: 1, max: 100, unit: "px"),
        Double("MainWindowBodyFontSize", "正文字号", null, "appearance", "字体", min: 1, max: 100, unit: "px"),
        Double("MainWindowEmphasizedFontSize", "强调字号", null, "appearance", "字体", min: 1, max: 100, unit: "px"),
        Double("MainWindowLargeFontSize", "大字号", null, "appearance", "字体", min: 1, max: 100, unit: "px"),
        Boolean("IsCustomForegroundColorEnabled", "启用自定义字体颜色", null, "appearance", "字体"),
        Color("CustomForegroundColor", "字体颜色", null, "appearance", "字体"),

        // ---------- 窗口 ----------
        Enum("WindowDockingLocation", "窗口停靠位置", "主界面停靠在屏幕上的位置。", "window", "位置与层级",
            [new(0, "左上角"), new(1, "中上侧"), new(2, "右上角"), new(3, "左下角"), new(4, "中下侧"), new(5, "右下角")]),
        Integer("WindowDockingOffsetX", "窗口向右偏移", null, "window", "位置与层级", min: -2000, max: 2000, unit: "px"),
        Integer("WindowDockingOffsetY", "窗口向下偏移", null, "window", "位置与层级", min: -2000, max: 2000, unit: "px"),
        Integer("WindowDockingMonitorIndex", "目标显示器", "0 为主显示器；指定的显示器不存在时显示在主显示器上。", "window", "位置与层级", min: 0, max: 16),
        Boolean("IsIgnoreWorkAreaEnabled", "忽略工作区", "主界面不再避开任务栏等工作区边缘。", "window", "位置与层级"),
        Enum("WindowLayer", "窗口层级", null, "window", "位置与层级", [new(0, "置底"), new(1, "置顶")]),
        Integer("WindowTopmostRecheckMode", "窗口层级设置频率", null, "window", "位置与层级", options: [new(0, "每 50ms"), new(1, "每 1ms")]),
        Boolean("IsCompatibleWindowTransparentEnabled", "启用兼容窗口透明模式", "遇到透明特效异常时开启；可能影响性能。", "window", "位置与层级"),
        Boolean("IsMouseInFadingEnabled", "指针移入淡化", "鼠标悬停在主界面上时降低不透明度，方便点击下层窗口。", "window", "交互"),
        Boolean("IsMouseInFadingReversed", "反转指针移入淡化", null, "window", "交互"),
        Boolean("IsMouseClickingEnabled", "启用鼠标点击", null, "window", "交互"),
        Boolean("UseRawInput", "使用原始输入", "以原始输入检测指针位置；检测异常时尝试切换。", "window", "交互"),
        Double("TouchInFadingDurationMs", "触屏点击后自动恢复时间", null, "window", "交互", min: 0, max: 10000, unit: "ms"),
        Boolean("IsScreenRecordingModeEnabled", "录制模式", "录屏时隐藏主界面但保留窗口。", "window", "交互"),
        Boolean("IsWindowCaptureBlockingEnabled", "阻止截图", "禁止其他程序截取 ClassIsland 窗口。", "window", "交互"),

        // ---------- 提醒 ----------
        Boolean("IsNotificationEnabled", "启用提醒", "关闭后 ClassIsland 不再显示任何提醒。", "notification", "提醒"),
        Boolean("IsSpeechEnabled", "启用提醒语音", null, "notification", "语音"),
        Boolean("AllowNotificationSpeech", "允许通知请求朗读", "允许远程通知请求启用语音朗读。", "notification", "语音"),
        Enum("SpeechSource", "语音来源", null, "notification", "语音", [new(0, "系统语音"), new(1, "EdgeTTS")]),
        String("SelectedSpeechProvider", "语音提供方", "语音提供方插件 Id。", "notification", "语音"),
        String("EdgeTtsVoiceName", "EdgeTTS 语音", "例如 zh-CN-XiaoxiaoNeural。", "notification", "语音"),
        Double("SpeechVolume", "语音音量", null, "notification", "语音", min: 0, max: 1, step: 0.05),
        Boolean("IsNotificationEffectEnabled", "启用提醒强调特效", null, "notification", "特效"),
        Boolean("AllowNotificationEffect", "允许通知请求启用特效", "允许远程通知请求启用提醒强调特效。", "notification", "特效"),
        Double("NotificationEffectRenderingScale", "特效渲染缩放", "降低数值可减轻特效渲染压力。", "notification", "特效", min: 0.1, max: 1.5, step: 0.05),
        Boolean("NotificationUseStandaloneEffectUiThread", "特效使用独立渲染线程", null, "notification", "特效"),
        Boolean("IsNotificationSoundEnabled", "启用提醒音效", null, "notification", "音效"),
        Boolean("AllowNotificationSound", "允许通知请求启用音效", "允许远程通知请求播放提醒音效。", "notification", "音效"),
        Double("NotificationSoundVolume", "音效音量", null, "notification", "音效", min: 0, max: 1, step: 0.05),
        String("NotificationSoundPath", "自定义音效文件", "留空使用默认音效。", "notification", "音效"),
        Boolean("IsNotificationTopmostEnabled", "置顶提醒", "提醒时临时置顶主界面。", "notification", "置顶"),
        Boolean("AllowNotificationTopmost", "允许通知请求置顶", "允许远程通知请求置顶主界面。", "notification", "置顶"),

        // ---------- 天气 ----------
        Enum("WeatherLocationSource", "天气位置来源", null, "weather", "位置", [new(0, "城市"), new(1, "坐标")]),
        String("CityId", "城市 ID", "和风天气城市代码，例如 weathercn:101010100。", "weather", "位置"),
        String("CityName", "城市名称", null, "weather", "位置"),
        Double("WeatherLongitude", "经度", null, "weather", "位置", min: -180, max: 180),
        Double("WeatherLatitude", "纬度", null, "weather", "位置", min: -90, max: 90),
        Boolean("AutoRefreshWeatherLocation", "自动刷新天气位置", null, "weather", "位置"),
        Boolean("NoTLSWeatherRequests", "天气请求不使用 TLS", "部分内网环境无法完成 TLS 握手时开启。", "weather", "位置"),
        StringList("ExcludedWeatherAlerts", "排除的气象预警", "不再提醒的预警类型。", "weather", "预警"),
        String("WeatherIconId", "天气图标模板", "天气图标提供方模板 Id。", "weather", "预警"),

        // ---------- 更新 ----------
        Integer("UpdateMode", "更新方式", null, "update", "更新", min: 0, max: 3),
        String("SelectedUpdateChannelV2", "更新渠道", "stable 为稳定版，beta 为测试版。", "update", "更新"),
        String("SelectedUpdateMirrorV2", "更新镜像", null, "update", "更新"),
        Boolean("AutoInstallUpdateNextStartup", "重启后自动安装更新", null, "update", "更新"),

        // ---------- 自动化 ----------
        Boolean("IsAutomationEnabled", "启用自动化", null, "automation", "自动化"),
        String("CurrentAutomationConfig", "自动化方案", "设备本地的自动化方案名称。", "automation", "自动化"),

        // ---------- 存储 ----------
        Boolean("IsAutoBackupEnabled", "启用自动备份", "定期备份课表等本地数据。", "storage", "备份"),
        Integer("AutoBackupIntervalDays", "自动备份间隔", null, "storage", "备份", min: 1, max: 365, unit: "天"),
        Integer("AutoBackupLimit", "自动备份保留数量", null, "storage", "备份", min: 1, max: 100),
        String("SelectedProfile", "当前课表档案文件", "设备使用的课表档案文件名（Config/Profiles 下）。", "storage", "课表档案"),

        // ---------- 隐私 ----------
        Boolean("IsReportingEnabled", "启用诊断数据上报", null, "privacy", "诊断"),

        // ---------- 插件 ----------
        String("OfficialSelectedMirror", "插件市场镜像", null, "plugins", "插件市场"),
        Boolean("IsPluginsAutoUpdateEnabled", "插件自动更新", null, "plugins", "插件市场"),
        Boolean("IsPluginsUpdateNotificationEnabled", "插件更新通知", null, "plugins", "插件市场"),
        Boolean("IgnoreSslForPluginMirrors", "忽略插件镜像 SSL 错误", null, "plugins", "插件市场"),

        // ---------- 主界面 ----------
        String("CurrentComponentConfig", "组件配置方案", "主界面使用的组件配置方案名；如需修改内容请使用控制页的“主界面文件”。", "components", "主界面"),

        // ---------- 调试 ----------
        Boolean("IsDebugEnabled", "启用调试", null, "debug", "调试", advanced: true),
        Boolean("IsDebugOptionsEnabled", "显示调试选项", null, "debug", "调试", advanced: true),
        Boolean("IsMainWindowDebugEnabled", "显示主界面调试信息", null, "debug", "调试", advanced: true),
        Boolean("IsDebugConsoleEnabled", "启用调试控制台", null, "debug", "调试", advanced: true),
        Double("DebugAnimationScale", "调试动画速度", null, "debug", "调试", min: 0.1, max: 10, step: 0.1, advanced: true),
    ];

    private static ClassIslandSettingDescriptor Boolean(string key, string label, string? description, string page, string? card,
        bool advanced = false) => new()
    {
        Key = key, Type = ClassIslandSettingType.Boolean, Label = label, Description = description,
        Page = page, Card = card, Advanced = advanced,
    };

    private static ClassIslandSettingDescriptor Integer(string key, string label, string? description, string page, string? card,
        double? min = null, double? max = null, double? step = null, string? unit = null,
        IReadOnlyList<ClassIslandSettingOption>? options = null, bool advanced = false) => new()
    {
        Key = key, Type = options is null ? ClassIslandSettingType.Integer : ClassIslandSettingType.Enum,
        Label = label, Description = description, Page = page, Card = card,
        Options = options, Min = min, Max = max, Step = step, Unit = unit, Advanced = advanced,
    };

    private static ClassIslandSettingDescriptor Enum(string key, string label, string? description, string page, string? card,
        IReadOnlyList<ClassIslandSettingOption> options, bool advanced = false) => new()
    {
        Key = key, Type = ClassIslandSettingType.Enum, Label = label, Description = description,
        Page = page, Card = card, Options = options, Advanced = advanced,
    };

    private static ClassIslandSettingDescriptor Double(string key, string label, string? description, string page, string? card,
        double? min = null, double? max = null, double? step = null, string? unit = null, bool advanced = false) => new()
    {
        Key = key, Type = ClassIslandSettingType.Double, Label = label, Description = description,
        Page = page, Card = card, Min = min, Max = max, Step = step, Unit = unit, Advanced = advanced,
    };

    private static ClassIslandSettingDescriptor String(string key, string label, string? description, string page, string? card,
        bool advanced = false) => new()
    {
        Key = key, Type = ClassIslandSettingType.String, Label = label, Description = description,
        Page = page, Card = card, Advanced = advanced,
    };

    private static ClassIslandSettingDescriptor Color(string key, string label, string? description, string page, string? card) => new()
    {
        Key = key, Type = ClassIslandSettingType.Color, Label = label, Description = description, Page = page, Card = card,
    };

    private static ClassIslandSettingDescriptor Date(string key, string label, string? description, string page, string? card) => new()
    {
        Key = key, Type = ClassIslandSettingType.Date, Label = label, Description = description, Page = page, Card = card,
    };

    private static ClassIslandSettingDescriptor StringList(string key, string label, string? description, string page, string? card) => new()
    {
        Key = key, Type = ClassIslandSettingType.StringList, Label = label, Description = description, Page = page, Card = card,
    };

    private static readonly Dictionary<string, ClassIslandSettingDescriptor> ItemIndex =
        Items.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static ClassIslandSettingDescriptor? Find(string key) =>
        ItemIndex.TryGetValue(key, out var descriptor) ? descriptor : null;

    public static IReadOnlyList<ClassIslandSettingDescriptor> ItemsForPage(string page) =>
        Items.Where(item => item.Page == page).ToList();

    /// <summary>颜色字符串：#RGB / #RGBA / #RRGGBB / #AARRGGBB。</summary>
    public static bool IsValidColor(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    /// <summary>
    /// 校验一次设置修改：键必须在目录中，值必须与目录类型匹配并在范围内。
    /// 返回错误消息；通过时返回 null。空字典视为合法（没有要修改的项）。
    /// </summary>
    public static string? ValidatePatch(IDictionary<string, JsonElement>? values)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count > MaxPatchValues) return $"一次最多修改 {MaxPatchValues} 个设置项。";
        foreach (var (key, value) in values)
        {
            if (Find(key) is not { } descriptor) return $"未知的 ClassIsland 设置项：{key}";
            if (ValidateValue(descriptor, value) is { } error) return error;
        }
        return null;
    }

    /// <summary>按目录类型校验单个 JSON 值。</summary>
    public static string? ValidateValue(ClassIslandSettingDescriptor descriptor, JsonElement value)
    {
        switch (descriptor.Type)
        {
            case ClassIslandSettingType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return $"设置项 {descriptor.Key} 需要布尔值";
                return null;
            case ClassIslandSettingType.Integer or ClassIslandSettingType.Enum:
                if (value.ValueKind != JsonValueKind.Number || !TryGetInt(value, out var intValue))
                    return $"设置项 {descriptor.Key} 需要整数";
                if (descriptor.Type == ClassIslandSettingType.Enum)
                {
                    if (descriptor.Options?.Any(option => option.Value == intValue) != true)
                        return $"设置项 {descriptor.Key} 的取值无效";
                    return null;
                }
                return CheckRange(descriptor, intValue);
            case ClassIslandSettingType.Double:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var doubleValue))
                    return $"设置项 {descriptor.Key} 需要数字";
                return CheckRange(descriptor, doubleValue);
            case ClassIslandSettingType.String:
                if (value.ValueKind != JsonValueKind.String) return $"设置项 {descriptor.Key} 需要文本";
                return null;
            case ClassIslandSettingType.Color:
                if (value.ValueKind != JsonValueKind.String || !IsValidColor(value.GetString() ?? string.Empty))
                    return $"设置项 {descriptor.Key} 需要形如 #RRGGBB 的颜色";
                return null;
            case ClassIslandSettingType.Date:
                if (value.ValueKind != JsonValueKind.String ||
                    !System.DateTime.TryParseExact(value.GetString(), "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out _))
                    return $"设置项 {descriptor.Key} 需要形如 yyyy-MM-dd 的日期";
                return null;
            case ClassIslandSettingType.StringList:
                if (value.ValueKind != JsonValueKind.Array ||
                    value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                    return $"设置项 {descriptor.Key} 需要文本列表";
                return null;
            default:
                return $"设置项 {descriptor.Key} 类型不支持";
        }
    }

    private static string? CheckRange(ClassIslandSettingDescriptor descriptor, double value)
    {
        if (descriptor.Min is { } min && value < min) return $"设置项 {descriptor.Key} 不能小于 {min}";
        if (descriptor.Max is { } max && value > max) return $"设置项 {descriptor.Key} 不能大于 {max}";
        return null;
    }

    private static bool TryGetInt(JsonElement value, out int result)
    {
        if (value.TryGetInt32(out result)) return true;
        // 允许 1.0 这类来自 JS 的整数值浮点。
        if (value.TryGetDouble(out var doubleValue) && Math.Abs(doubleValue - Math.Round(doubleValue)) < double.Epsilon)
        {
            result = (int)Math.Round(doubleValue);
            return true;
        }
        return false;
    }
}

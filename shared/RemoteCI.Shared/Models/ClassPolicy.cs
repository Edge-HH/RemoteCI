using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 班级策略：限制教室端 ClassIsland 的编辑、调试与退出入口。
/// 字段与原版 ClassIsland 集控服务器的 ManagementPolicy 一致（默认值相同），
/// 另加 RemoteCI 自己的连接信息与插件设置页开关；由服务端按班级保存并下发。
/// </summary>
public sealed class ClassPolicy
{
    /// <summary>禁止编辑课表：不能创建、删除、编辑课表，也不能从 Excel 导入；临时换课不受影响。</summary>
    [JsonPropertyName("disableProfileClassPlanEditing")]
    public bool DisableProfileClassPlanEditing { get; set; }

    /// <summary>禁止编辑时间表：不能创建、删除、编辑时间表，也不能从 Excel 导入。</summary>
    [JsonPropertyName("disableProfileTimeLayoutEditing")]
    public bool DisableProfileTimeLayoutEditing { get; set; }

    /// <summary>禁止编辑科目：不能创建、删除、编辑科目。</summary>
    [JsonPropertyName("disableProfileSubjectsEditing")]
    public bool DisableProfileSubjectsEditing { get; set; }

    /// <summary>禁止编辑档案：不能编辑档案内任何内容，也不能从 Excel 导入；临时换课不受影响。</summary>
    [JsonPropertyName("disableProfileEditing")]
    public bool DisableProfileEditing { get; set; }

    /// <summary>禁止编辑应用设置：不能调整 ClassIsland 设置，已保存的设置不受影响。</summary>
    [JsonPropertyName("disableSettingsEditing")]
    public bool DisableSettingsEditing { get; set; }

    /// <summary>禁止自定义启动加载界面；已设置的内容会被清除。</summary>
    [JsonPropertyName("disableSplashCustomize")]
    public bool DisableSplashCustomize { get; set; }

    /// <summary>禁用调试菜单：不能进入调试页面。</summary>
    [JsonPropertyName("disableDebugMenu")]
    public bool DisableDebugMenu { get; set; }

    /// <summary>
    /// 禁止插件设置：ClassIsland 设置界面不显示自带的“插件”页，不能安装、卸载、启用或禁用插件。
    /// ClassIsland 集控没有此项，由 RemoteCI 插件自己执行；已安装插件各自的设置页不受影响。
    /// </summary>
    [JsonPropertyName("disablePluginsSettings")]
    public bool DisablePluginsSettings { get; set; }

    /// <summary>允许退出 ClassIsland 内置集控；关闭后用户不能自行退出。</summary>
    [JsonPropertyName("allowExitManagement")]
    public bool AllowExitManagement { get; set; } = true;

    /// <summary>允许在插件设置页修改服务器地址、端口与连接码；关闭后连接信息只读。</summary>
    [JsonPropertyName("allowChangeConnection")]
    public bool AllowChangeConnection { get; set; } = true;

    /// <summary>允许打开 RemoteCI 插件设置页；关闭后设置页只显示受限提示。</summary>
    [JsonPropertyName("allowPluginSettings")]
    public bool AllowPluginSettings { get; set; } = true;

    /// <summary>是否与默认值完全一致；用于班级列表与批量结果展示“是否受限”。</summary>
    [JsonIgnore]
    public bool IsDefault =>
        !DisableProfileClassPlanEditing &&
        !DisableProfileTimeLayoutEditing &&
        !DisableProfileSubjectsEditing &&
        !DisableProfileEditing &&
        !DisableSettingsEditing &&
        !DisableSplashCustomize &&
        !DisableDebugMenu &&
        !DisablePluginsSettings &&
        AllowExitManagement &&
        AllowChangeConnection &&
        AllowPluginSettings;

    public ClassPolicy Clone() => (ClassPolicy)MemberwiseClone();

    /// <summary>解析服务端保存的策略 JSON；为空或损坏时回落到默认值（不限制）。</summary>
    public static ClassPolicy FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ClassPolicy();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ClassPolicy>(json, JsonDefaults.Options)
                ?? new ClassPolicy();
        }
        catch (System.Text.Json.JsonException)
        {
            return new ClassPolicy();
        }
    }
}

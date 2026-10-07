using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// 扩展分组，通常对应一个 ClassIsland 插件：WebUI 控制页与批量控制页把同组扩展功能归在一起显示。
/// 声明了 <see cref="Settings"/> 的分组还会在 WebUI 获得独立设置页，管理员可以按班级、分组或设备
/// 批量修改设置，修改由 <see cref="ApplySettingsAsync"/> 在每台设备上实际写入。
/// </summary>
public interface IRemoteCiExtensionGroup
{
    /// <summary>全局唯一分组 Id；规则与扩展 Id 相同（非空、无首尾空白、不超过 200 个字符）。</summary>
    string Id { get; }

    /// <summary>WebUI 中的分组标题，一般使用插件名称。</summary>
    string DisplayName { get; }

    /// <summary>可选分组说明。</summary>
    string? Description { get; }

    /// <summary>可选图标名，取值与扩展图标相同。</summary>
    string? Icon { get; }

    /// <summary>设置字段描述；为空表示该分组没有设置页，只用于归类扩展功能。</summary>
    IReadOnlyList<ExtensionParameter> Settings { get; }

    /// <summary>
    /// 读取本机当前设置值（键为设置字段 Key，值统一为字符串，开关用 "true"/"false"）。
    /// RemoteCI 在连接建立、分组注册和设置变化时调用，把结果同步给服务端用于预填和差异对比。
    /// </summary>
    IReadOnlyDictionary<string, string?> GetSettings();

    /// <summary>
    /// 应用远程修改的设置。<paramref name="values"/> 只包含本次要修改的字段，且已按字段声明完成类型校验；
    /// 未出现的字段应保持不变。返回的 CommandResult 会作为回执展示在 WebUI 上。
    /// </summary>
    Task<CommandResult> ApplySettingsAsync(
        ExtensionExecutionContext context,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken);
}

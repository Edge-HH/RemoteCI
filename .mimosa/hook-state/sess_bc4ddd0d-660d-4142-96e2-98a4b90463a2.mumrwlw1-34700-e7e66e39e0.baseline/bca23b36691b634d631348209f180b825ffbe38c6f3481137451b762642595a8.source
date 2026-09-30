using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// 其他 ClassIsland 插件向 RemoteCI 注册的自定义远程功能。
/// 注册后功能会出现在 WearOS 控制子菜单底部，点击后由插件执行端调用 <see cref="ExecuteAsync"/>。
/// </summary>
public interface IRemoteCiExtension
{
    /// <summary>全局唯一扩展 Id；必须非空、无首尾空白且不超过 200 个字符。</summary>
    string Id { get; }

    /// <summary>手表控制菜单展示的文案。</summary>
    string DisplayName { get; }

    /// <summary>兼容旧扩展的声明字段；当前调用统一使用 RunExtensions 权限。</summary>
    UserPermissions RequiredPermission { get; }

    /// <summary>可选 Material 图标名，取值见文档站「接入扩展 - 扩展图标名」；未知或缺失时手表回退为纯文字。</summary>
    string? Icon { get; }

    /// <summary>可选参数表单描述；为空时手表点击后直接执行，否则先进入参数页面。</summary>
    IReadOnlyList<ExtensionParameter> Parameters { get; }

    /// <summary>执行远程功能；异常统一由 RemoteCI 转为 INTERNAL_ERROR 回执。</summary>
    Task<CommandResult> ExecuteAsync(
        ExtensionExecutionContext context,
        IReadOnlyDictionary<string, string?> args,
        CancellationToken cancellationToken);
}

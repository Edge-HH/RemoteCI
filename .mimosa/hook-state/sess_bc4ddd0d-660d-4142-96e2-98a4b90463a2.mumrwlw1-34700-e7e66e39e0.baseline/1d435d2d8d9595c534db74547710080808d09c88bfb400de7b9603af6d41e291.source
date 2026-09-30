using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// <see cref="IRemoteCiExtension"/> 的默认实现，只需实现 Id、DisplayName、
/// 兼容字段 RequiredPermission 与 ExecuteAsync，其余成员按无图标、无参数处理。
/// </summary>
public abstract class RemoteCiExtensionBase : IRemoteCiExtension
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract UserPermissions RequiredPermission { get; }

    public virtual string? Icon => null;

    public virtual IReadOnlyList<ExtensionParameter> Parameters => [];

    public abstract Task<CommandResult> ExecuteAsync(
        ExtensionExecutionContext context,
        IReadOnlyDictionary<string, string?> args,
        CancellationToken cancellationToken);
}

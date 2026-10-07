using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// <see cref="IRemoteCiExtensionGroup"/> 的默认实现：只需实现 Id 与 DisplayName 即可作为纯分组使用；
/// 需要设置页时再覆盖 Settings、GetSettings 与 ApplySettingsAsync。
/// </summary>
public abstract class RemoteCiExtensionGroupBase : IRemoteCiExtensionGroup
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public virtual string? Description => null;

    public virtual string? Icon => null;

    public virtual IReadOnlyList<ExtensionParameter> Settings => [];

    public virtual IReadOnlyDictionary<string, string?> GetSettings() => new Dictionary<string, string?>();

    public virtual Task<CommandResult> ApplySettingsAsync(
        ExtensionExecutionContext context,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "该扩展分组没有可修改的设置"));
}

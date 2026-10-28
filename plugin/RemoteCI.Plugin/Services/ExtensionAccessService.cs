using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>接入 RemoteCI 扩展注册表的 ClassIsland 插件；无法对应到已加载插件时以程序集名标识。</summary>
public sealed record ExtensionCaller(string PluginId, string Name);

public enum ExtensionAccessState
{
    Allowed = 1,
    Denied = 2,
}

/// <summary>某个插件的扩展接入授权记录。</summary>
public sealed class ExtensionAccessEntry
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public ExtensionAccessState State { get; set; }

    /// <summary>服务端要求保持授权：设备端不能解除，直到服务端解除保持。</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    public ExtensionAccessEntry Clone() => (ExtensionAccessEntry)MemberwiseClone();
}

/// <summary>询问用户是否允许插件接入；返回 null 表示没能询问（如没有界面），本次运行内保持待定。</summary>
public interface IExtensionAccessPrompt
{
    Task<bool?> AskAsync(ExtensionCaller caller);
}

/// <summary>
/// RemoteCI 扩展接入授权：其他插件首次注册扩展或扩展分组时询问用户，结果落盘。
/// 未授权插件的注册仍被接受（不影响其自身运行），只是不会出现在扩展清单中，也不能被远程调用。
/// 远程安装可预先授权；服务端可“保持授权”，此时设备端不能解除。
/// </summary>
public sealed class ExtensionAccessService
{
    private readonly string? _path;
    private readonly IExtensionAccessPrompt? _prompt;
    private readonly object _lock = new();
    private readonly Dictionary<string, ExtensionAccessEntry> _entries;
    private readonly HashSet<string> _prompting = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionAccessService(string? path, IExtensionAccessPrompt? prompt)
    {
        _path = path;
        _prompt = prompt;
        _entries = Load(path);
    }

    /// <summary>任一插件的授权状态变化后触发，注册表据此重新广播扩展清单。</summary>
    public event Action? Changed;

    public IReadOnlyList<ExtensionAccessEntry> Snapshot()
    {
        lock (_lock)
            return _entries.Values.Select(x => x.Clone()).OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
    }

    public bool IsAllowed(string pluginId)
    {
        lock (_lock)
            return _entries.TryGetValue(pluginId, out var entry) && entry.State == ExtensionAccessState.Allowed;
    }

    /// <summary>
    /// 插件尝试接入时调用：已有决定直接返回；首次接入时在后台询问用户，同一插件同时只询问一次。
    /// </summary>
    public bool RequestAccess(ExtensionCaller caller)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(caller.PluginId, out var entry))
            {
                if (!string.Equals(entry.Name, caller.Name, StringComparison.Ordinal) && caller.Name.Length > 0)
                {
                    entry.Name = caller.Name;
                    Persist();
                }
                return entry.State == ExtensionAccessState.Allowed;
            }
            if (_prompt is null || !_prompting.Add(caller.PluginId)) return false;
        }
        _ = AskAsync(caller);
        return false;
    }

    private async Task AskAsync(ExtensionCaller caller)
    {
        bool? allowed;
        try { allowed = await _prompt!.AskAsync(caller); }
        catch (Exception) { allowed = null; }

        lock (_lock)
        {
            _prompting.Remove(caller.PluginId);
            // 询问期间可能已被远程预授权或保持授权，以已有决定为准。
            if (allowed is null || _entries.ContainsKey(caller.PluginId)) return;
            _entries[caller.PluginId] = new ExtensionAccessEntry
            {
                PluginId = caller.PluginId,
                Name = caller.Name,
                State = allowed.Value ? ExtensionAccessState.Allowed : ExtensionAccessState.Denied,
                UpdatedAt = DateTimeOffset.Now,
            };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>远程安装时预授权；已保持授权或已允许的保持不变，已拒绝的改为允许。</summary>
    public void Grant(IEnumerable<string> pluginIds) => Mutate(pluginIds, entry =>
    {
        if (entry.State == ExtensionAccessState.Allowed) return false;
        entry.State = ExtensionAccessState.Allowed;
        return true;
    });

    /// <summary>本机设置页：允许或拒绝（解除授权）某插件；服务端保持授权的插件不能被拒绝。</summary>
    public bool TrySetLocal(ExtensionCaller caller, bool allowed, out string? error)
    {
        var state = allowed ? ExtensionAccessState.Allowed : ExtensionAccessState.Denied;
        lock (_lock)
        {
            if (_entries.TryGetValue(caller.PluginId, out var entry))
            {
                if (!allowed && entry.Locked)
                {
                    error = "服务端要求保持该插件的授权，不能解除";
                    return false;
                }
                error = null;
                if (entry.State == state) return true;
                entry.State = state;
            }
            else
            {
                // 弹窗被关闭而未决定的插件，也可以直接在设置页决定。
                error = null;
                entry = new ExtensionAccessEntry { PluginId = caller.PluginId, Name = caller.Name, State = state };
                _entries[caller.PluginId] = entry;
            }
            entry.UpdatedAt = DateTimeOffset.Now;
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>执行服务端下发的保持 / 解除保持授权。</summary>
    public CommandResult Apply(ExtensionAccessRequest? request)
    {
        if (request is null || !Enum.IsDefined(request.Action))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少扩展接入授权参数");
        var ids = NormalizeIds(request.PluginIds);
        if (ids.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "请至少选择一个插件");
        if (ids.Count > ExtensionAccessRequest.MaxPlugins)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"一次最多设置 {ExtensionAccessRequest.MaxPlugins} 个插件");

        var locked = request.Action == ExtensionAccessAction.Lock;
        Mutate(ids, entry =>
        {
            var changed = entry.Locked != locked || (locked && entry.State != ExtensionAccessState.Allowed);
            entry.Locked = locked;
            if (locked) entry.State = ExtensionAccessState.Allowed;
            return changed;
        });
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = locked
                ? $"已授权并保持 {ids.Count} 个插件接入 RemoteCI，设备端不能解除"
                : $"已解除保持 {ids.Count} 个插件，授权状态不变，设备端可以解除",
        };
    }

    private void Mutate(IEnumerable<string> pluginIds, Func<ExtensionAccessEntry, bool> change)
    {
        var changed = false;
        lock (_lock)
        {
            foreach (var id in NormalizeIds(pluginIds))
            {
                if (!_entries.TryGetValue(id, out var entry))
                {
                    // 尚未运行过的插件：先登记为拒绝再交给变更函数决定，名称在插件首次接入时补上。
                    entry = new ExtensionAccessEntry { PluginId = id, Name = id, State = ExtensionAccessState.Denied };
                    if (!change(entry)) continue;
                    _entries[id] = entry;
                }
                else if (!change(entry))
                {
                    continue;
                }
                entry.UpdatedAt = DateTimeOffset.Now;
                changed = true;
            }
            if (changed) Persist();
        }
        if (changed) Changed?.Invoke();
    }

    private static List<string> NormalizeIds(IEnumerable<string>? values) => (values ?? [])
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200)
        .Cast<string>()
        .Where(value => !string.Equals(value, PluginManagementService.RemoteCiPluginId, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void Persist()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries.Values.ToList(), JsonDefaults.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去时只在本次运行内生效，下次启动会重新询问。
        }
    }

    private static Dictionary<string, ExtensionAccessEntry> Load(string? path)
    {
        var result = new Dictionary<string, ExtensionAccessEntry>(StringComparer.OrdinalIgnoreCase);
        if (path is null) return result;
        try
        {
            if (!File.Exists(path)) return result;
            var entries = JsonSerializer.Deserialize<List<ExtensionAccessEntry>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.PluginId) || !Enum.IsDefined(entry.State)) continue;
                result[entry.PluginId] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }
}

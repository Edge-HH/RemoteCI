using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>服务端最近一次下发的应用守护策略；落盘缓存，ClassIsland 离线启动时也按它部署守护进程。</summary>
public sealed class AppGuardStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppGuardPolicy? _current;

    public AppGuardStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event Action? Changed;

    /// <summary>从未收到过策略时为 null：不部署也不清理，保持安装插件前的状态。</summary>
    public AppGuardPolicy? Current
    {
        get { lock (_lock) return _current; }
    }

    public void Apply(AppGuardPolicy policy)
    {
        var normalized = policy.Normalized();
        lock (_lock)
        {
            if (_current is not null &&
                JsonSerializer.Serialize(_current, JsonDefaults.Options) == JsonSerializer.Serialize(normalized, JsonDefaults.Options))
                return;
            _current = normalized;
            Persist(normalized);
        }
        Changed?.Invoke();
    }

    private void Persist(AppGuardPolicy policy)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(policy, JsonDefaults.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去时只在本次运行内生效。
        }
    }

    private static AppGuardPolicy? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppGuardPolicy>(File.ReadAllText(path), JsonDefaults.Options)?.Normalized()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>从信封中取出应用守护策略；单独成类以便不经网络直接测试。</summary>
public static class AppGuardPolicyMessage
{
    public static bool TryRead(Envelope envelope, out AppGuardPolicy policy)
    {
        policy = null!;
        if (envelope.Type != Protocol.MessageTypeAppGuardPolicy || envelope.Payload is null) return false;
        try
        {
            policy = JsonSerializer.Deserialize<AppGuardPolicy>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return policy is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

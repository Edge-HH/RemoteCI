using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>服务端最近一次下发的班级策略；落盘缓存，ClassIsland 离线启动时也按它限制。</summary>
public sealed class ClassPolicyStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private ClassPolicy? _current;

    public ClassPolicyStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    /// <summary>从未收到过策略时为 null：不去改动宿主已有的策略。</summary>
    public ClassPolicy? Current
    {
        get { lock (_lock) return _current; }
    }

    public void Apply(ClassPolicy policy)
    {
        var normalized = policy.Clone();
        lock (_lock)
        {
            if (_current is not null &&
                JsonSerializer.Serialize(_current, JsonDefaults.Options) ==
                JsonSerializer.Serialize(normalized, JsonDefaults.Options))
                return;
            _current = normalized;
            Persist(normalized);
        }
    }

    private void Persist(ClassPolicy policy)
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

    private static ClassPolicy? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ClassPolicy>(File.ReadAllText(path), JsonDefaults.Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>从信封中取出班级策略；单独成类以便不经网络直接测试。</summary>
public static class ClassPolicyMessage
{
    public static bool TryRead(Envelope envelope, out ClassPolicy policy)
    {
        policy = null!;
        if (envelope.Type != Protocol.MessageTypeClassPolicy || envelope.Payload is null) return false;
        try
        {
            policy = JsonSerializer.Deserialize<ClassPolicy>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return policy is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

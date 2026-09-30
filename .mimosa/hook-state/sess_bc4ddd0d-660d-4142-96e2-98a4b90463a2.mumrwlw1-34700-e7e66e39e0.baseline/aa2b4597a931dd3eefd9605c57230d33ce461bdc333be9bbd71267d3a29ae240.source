using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCI.Shared;

/// <summary>
/// 三端统一的 JSON 序列化选项：小驼峰、忽略 null，与协议文档一致。
/// 插件、服务端、测试共用，保证线上/线下行为一致。
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

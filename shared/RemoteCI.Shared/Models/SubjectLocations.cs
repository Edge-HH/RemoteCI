using System.Text.Json.Nodes;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 科目地点：ClassIsland 科目没有地点字段，RemoteCI 把它存在档案科目的附加数据里
/// （<c>Subject.AttachedObjects["<see cref="AttachedObjectId"/>"] = {"Location": "机房"}</c>），
/// 随档案收集、下发与教室端保存原样保留；ClassIsland 自身不显示它。空地点表示在本班教室上课，不显示。
/// </summary>
public static class SubjectLocations
{
    /// <summary>RemoteCI 在科目附加数据中使用的固定键。</summary>
    public static readonly Guid AttachedObjectId = Guid.Parse("5b7e2c1a-9d43-4f6e-a8b1-3c0d7e9f2a64");

    public const string AttachedObjectsField = "AttachedObjects";
    public const string LocationField = "Location";

    /// <summary>地点长度上限；服务端、插件与档案编辑器共用。</summary>
    public const int MaxLength = 100;

    /// <summary>去首尾空白后的地点；空白视为未设置。</summary>
    public static string? Normalize(string? location)
    {
        var value = location?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>读取档案 JSON 中某个科目对象的地点；字段名大小写不敏感，与 ClassIsland 的读取一致。</summary>
    public static string? Read(JsonObject subject)
    {
        if (Field(subject, AttachedObjectsField) is not JsonObject attached) return null;
        foreach (var (key, value) in attached)
            if (Guid.TryParse(key, out var id) && id == AttachedObjectId && value is JsonObject data &&
                Field(data, LocationField) is JsonValue text && text.TryGetValue<string>(out var location))
                return Normalize(location);
        return null;
    }

    /// <summary>写入或清除档案 JSON 中某个科目对象的地点，保留其他附加数据。</summary>
    public static void Write(JsonObject subject, string? location)
    {
        location = Normalize(location);
        var attached = Field(subject, AttachedObjectsField) as JsonObject;
        if (attached is not null)
            foreach (var key in attached.Select(x => x.Key).Where(key => Guid.TryParse(key, out var id) && id == AttachedObjectId).ToList())
                attached.Remove(key);
        if (location is null) return;
        if (attached is null)
        {
            attached = new JsonObject();
            subject[AttachedObjectsField] = attached;
        }
        attached[AttachedObjectId.ToString()] = new JsonObject { [LocationField] = location };
    }

    private static JsonNode? Field(JsonObject node, string name) =>
        node.FirstOrDefault(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

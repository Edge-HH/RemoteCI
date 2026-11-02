using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>科目附加数据中保存的地点；序列化为 <c>{"Location": "机房"}</c>，与服务端 <see cref="SubjectLocations"/> 读写的格式一致。</summary>
public sealed class SubjectLocationAttachment
{
    public string Location { get; set; } = string.Empty;
}

/// <summary>
/// 科目地点设置执行的纯核心（经防腐层注入档案写入，可单元测试）：写入或清除科目附加数据中的地点，保存失败回滚。
/// </summary>
internal static class SubjectLocationExecutor
{
    private static readonly Guid AttachedKey = SubjectLocations.AttachedObjectId;

    public static CommandResult? Validate(SubjectLocationRequest? request)
    {
        if (request is null || request.SubjectId == Guid.Empty)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "科目无效");
        if (request.Location is { Length: > SubjectLocations.MaxLength })
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"科目地点不能超过 {SubjectLocations.MaxLength} 个字符");
        return null;
    }

    /// <summary>
    /// 读取科目的地点；没有或附加数据无法识别时返回 null。只读不写：宿主的 GetAttachedObject 会把反序列化结果写回字典，
    /// 课表上报可能不在 UI 线程，不能触发档案对象的变更通知。
    /// </summary>
    public static string? Read(Subject subject)
    {
        try
        {
            if (!subject.AttachedObjects.TryGetValue(AttachedKey, out var value)) return null;
            var location = value switch
            {
                SubjectLocationAttachment attachment => attachment.Location,
                JsonElement { ValueKind: JsonValueKind.Object } json => json.EnumerateObject()
                    .FirstOrDefault(x => x.Name.Equals(SubjectLocations.LocationField, StringComparison.OrdinalIgnoreCase)) is
                    { Value.ValueKind: JsonValueKind.String } property ? property.Value.GetString() : null,
                JsonObject node => SubjectLocations.Read(new JsonObject
                {
                    [SubjectLocations.AttachedObjectsField] = new JsonObject { [AttachedKey.ToString()] = node.DeepClone() },
                }),
                _ => null,
            };
            return SubjectLocations.Normalize(location);
        }
        catch (Exception)
        {
            // 附加数据被其他程序改成了无法识别的结构：按未设置处理，不影响课表上报。
            return null;
        }
    }

    public static CommandResult Apply(
        SubjectLocationRequest request,
        IProfileWriteOperations profile,
        Action<Exception>? onSaveFailure = null)
    {
        if (!profile.Subjects.TryGetValue(request.SubjectId, out var subject))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "科目不存在");
        var location = SubjectLocations.Normalize(request.Location);
        if (Read(subject) == location)
            return BuildSuccessResult(location);
        var hadPrevious = subject.AttachedObjects.TryGetValue(AttachedKey, out var previous);
        Write(subject, location);
        try
        {
            profile.SaveProfile();
        }
        catch (Exception ex)
        {
            if (hadPrevious) subject.AttachedObjects[AttachedKey] = previous;
            else subject.AttachedObjects.Remove(AttachedKey);
            onSaveFailure?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, "ClassIsland 保存科目地点失败，操作未确认");
        }
        return BuildSuccessResult(location);
    }

    private static void Write(Subject subject, string? location)
    {
        if (location is null) subject.AttachedObjects.Remove(AttachedKey);
        else subject.WriteAttachedObject(SubjectLocations.AttachedObjectId, new SubjectLocationAttachment { Location = location });
    }

    private static CommandResult BuildSuccessResult(string? location) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = location is null ? "已清除该科目的上课地点" : $"已将该科目的上课地点设置为 {location}",
    };
}

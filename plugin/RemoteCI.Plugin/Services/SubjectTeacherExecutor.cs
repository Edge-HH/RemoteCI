using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 科目教师设置执行的纯核心（不依赖 UI 线程与 ClassIsland 具体服务，经防腐层注入依赖），
/// 覆盖教师名长度校验与保存失败回滚，可单元测试。
/// </summary>
internal static class SubjectTeacherExecutor
{
    public static CommandResult? Validate(SubjectTeacherRequest? request)
    {
        if (request is null || request.SubjectId == Guid.Empty)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "科目无效");
        if (request.TeacherName is { Length: > SubjectTeacherRequest.MaxTeacherNameLength })
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"教师名不能超过 {SubjectTeacherRequest.MaxTeacherNameLength} 个字符");
        return null;
    }

    public static CommandResult Apply(
        SubjectTeacherRequest request,
        IProfileWriteOperations profile,
        Action<Exception>? onSaveFailure = null)
    {
        if (!profile.Subjects.TryGetValue(request.SubjectId, out var subject))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "科目不存在");
        var name = request.TeacherName?.Trim() ?? string.Empty;
        if (string.Equals(subject.TeacherName, name, StringComparison.Ordinal))
            return BuildSuccessResult(name);
        var previous = subject.TeacherName;
        subject.TeacherName = name;
        try
        {
            profile.SaveProfile();
        }
        catch (Exception ex)
        {
            subject.TeacherName = previous;
            onSaveFailure?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, "ClassIsland 保存科目教师失败，操作未确认");
        }
        return BuildSuccessResult(name);
    }

    private static CommandResult BuildSuccessResult(string name) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = name.Length == 0 ? "已清除该科目的授课教师" : $"已将该科目的授课教师设置为 {name}",
    };
}

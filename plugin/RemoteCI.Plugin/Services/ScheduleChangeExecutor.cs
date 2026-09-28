using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 换课执行的纯核心（不依赖 UI 线程与 ClassIsland 具体服务，经防腐层注入依赖），
/// 覆盖修订号并发控制、临时课表层创建与保存失败回滚，可单元测试。
/// </summary>
internal static class ScheduleChangeExecutor
{
    public static CommandResult Apply(
        DateTime date,
        ScheduleChangeRequest request,
        ScheduleCatalog catalog,
        IScheduleBackend backend,
        IProfileWriteOperations profile,
        Action<Exception>? onSaveFailure = null)
    {
        var before = catalog.BuildDay(date);
        if (GetPreconditionFailure(date, before, request) is { } precondition)
            return precondition;
        if (ValidateMutation(before.Courses.Count, request, profile) is { } beforeValidation)
            return beforeValidation;

        var plan = GetWritablePlan(date, request.Permanent, backend, profile);
        if (plan is null)
            return CommandResult.Failure(CommandResultCodes.ScheduleUnavailable, $"{date:yyyy-MM-dd} 无法创建临时课表层");
        if (ValidateMutation(plan.Classes.Count, request, profile) is { } validationFailure)
            return validationFailure;

        var mutation = ScheduleMutation.Create(plan.Classes, request);
        mutation.Apply();

        try
        {
            profile.SaveProfile();
        }
        catch (Exception ex)
        {
            mutation.Rollback();
            onSaveFailure?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, "ClassIsland 保存课表失败，操作未确认");
        }

        return BuildSuccessResult(catalog.BuildDay(date), request);
    }

    private static CommandResult? GetPreconditionFailure(
        DateTime date, ScheduleDay before, ScheduleChangeRequest request)
    {
        if (!before.Enabled)
            return CommandResult.Failure(CommandResultCodes.ScheduleUnavailable, $"{date:yyyy-MM-dd} 没有可编辑课表");
        if (string.Equals(before.Revision, request.ExpectedRevision, StringComparison.Ordinal))
            return null;
        return new CommandResult
        {
            Success = false,
            Code = CommandResultCodes.ScheduleStale,
            Message = "课表已被其他管理者修改，请刷新后重新确认",
            ScheduleRevision = before.Revision,
        };
    }

    private static CommandResult? ValidateMutation(
        int classCount, ScheduleChangeRequest request, IProfileWriteOperations profile)
    {
        var error = ScheduleMutation.Validate(
            classCount, request, subjectId => profile.Subjects.ContainsKey(subjectId));
        return error is null ? null : CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
    }

    private static CommandResult BuildSuccessResult(ScheduleDay after, ScheduleChangeRequest request) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = request.Permanent
            ? request.Mode == ScheduleChangeMode.Exchange ? "两节课程已永久交换" : "课程已永久替换"
            : request.Mode == ScheduleChangeMode.Exchange ? "两节课程已临时交换" : "课程已临时替换",
        ScheduleRevision = after.Revision,
    };

    /// <summary>取得可写的课表层：已是临时层直接使用，否则创建临时层并取回。</summary>
    internal static ClassPlan? GetWritablePlan(
        DateTime date, bool permanent, IScheduleBackend backend, IProfileWriteOperations profile)
    {
        var plan = backend.GetClassPlan(date, out var planId);
        if (plan is null || planId is null) return null;
        if (permanent) return plan;
        if (plan.IsOverlay) return plan;
        var overlayId = profile.CreateTempClassPlan(planId.Value, date);
        if (overlayId is null) return null;
        return backend.GetClassPlan(date, out var refreshedId) is { IsOverlay: true } refreshed
            ? refreshed
            : profile.ClassPlans.GetValueOrDefault(refreshedId ?? overlayId.Value);
    }
}

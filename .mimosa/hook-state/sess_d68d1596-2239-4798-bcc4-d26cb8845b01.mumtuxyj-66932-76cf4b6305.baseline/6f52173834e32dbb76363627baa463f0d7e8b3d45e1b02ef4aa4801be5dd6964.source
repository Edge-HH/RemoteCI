using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 封装一次可回滚的课表对象变更，确保 Profile 保存失败时不会把未持久化状态留在运行中的 ClassIsland。
/// </summary>
internal sealed class ScheduleMutation
{
    private readonly ClassInfo _source;
    private readonly ClassInfo? _target;
    private readonly Guid _sourceSubject;
    private readonly Guid? _targetSubject;
    private readonly bool _sourceChanged;
    private readonly bool _targetChanged;
    private readonly Guid? _replacementSubject;

    private ScheduleMutation(ClassInfo source, ClassInfo? target, Guid? replacementSubject)
    {
        _source = source;
        _target = target;
        _sourceSubject = source.SubjectId;
        _targetSubject = target?.SubjectId;
        _sourceChanged = source.IsChangedClass;
        _targetChanged = target?.IsChangedClass ?? false;
        _replacementSubject = replacementSubject;
    }

    public static string? Validate(
        int courseCount,
        ScheduleChangeRequest request,
        Func<Guid, bool> subjectExists)
    {
        if (request.SourceIndex < 0 || request.SourceIndex >= courseCount)
            return "原节次超出课表范围";

        return request.Mode switch
        {
            ScheduleChangeMode.Exchange when request.TargetIndex is not { } targetIndex ||
                targetIndex < 0 || targetIndex >= courseCount || targetIndex == request.SourceIndex => "目标节次无效",
            ScheduleChangeMode.Replace when request.ReplacementSubjectId is not { } subjectId ||
                !subjectExists(subjectId) => "替换科目不存在",
            ScheduleChangeMode.Exchange or ScheduleChangeMode.Replace => null,
            _ => "换课模式无效",
        };
    }

    public static ScheduleMutation Create(IReadOnlyList<ClassInfo> classes, ScheduleChangeRequest request) =>
        request.Mode == ScheduleChangeMode.Exchange
            ? new ScheduleMutation(classes[request.SourceIndex], classes[request.TargetIndex!.Value], null)
            : new ScheduleMutation(classes[request.SourceIndex], null, request.ReplacementSubjectId);

    public void Apply()
    {
        if (_target is not null)
        {
            (_source.SubjectId, _target.SubjectId) = (_target.SubjectId, _source.SubjectId);
            _target.IsChangedClass = true;
        }
        else
        {
            _source.SubjectId = _replacementSubject!.Value;
        }
        _source.IsChangedClass = true;
    }

    public void Rollback()
    {
        _source.SubjectId = _sourceSubject;
        _source.IsChangedClass = _sourceChanged;
        if (_target is null) return;
        _target.SubjectId = _targetSubject!.Value;
        _target.IsChangedClass = _targetChanged;
    }
}

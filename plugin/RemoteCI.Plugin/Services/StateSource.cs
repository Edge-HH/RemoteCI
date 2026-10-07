using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 高频状态读取的最小防腐层：隔离 ClassIsland 的 ILessonsService
/// （其含 internal 成员，外部程序集无法实现假对象），让快照构建与事件收集可单元测试。
/// </summary>
public interface IStateSource
{
    Subject? CurrentSubject { get; }
    Subject NextClassSubject { get; }
    TimeState CurrentState { get; }
    TimeLayoutItem CurrentTimeLayoutItem { get; }
    TimeLayoutItem NextClassTimeLayoutItem { get; }
    ClassPlan? CurrentClassPlan { get; }
    bool IsClassPlanEnabled { get; }
    bool IsClassPlanLoaded { get; }
    TimeSpan OnClassLeftTime { get; }
    TimeSpan OnBreakingTimeLeftTime { get; }
    bool IsLessonConfirmed { get; }
    /// <summary>ClassIsland 上课提醒为下一节课生效的“准备上课”提前量；宿主不可用时为 null。</summary>
    TimeSpan? ClassPreparingDuration { get; }

    event EventHandler? OnClass;
    event EventHandler? OnBreakingTime;
    event EventHandler? OnAfterSchool;
    event EventHandler? CurrentTimeStateChanged;
    event EventHandler? PostMainTimerTicked;
}

public sealed class StateSourceAdapter(
    ILessonsService lessons,
    IProfileService profiles,
    IServiceProvider services) : IStateSource
{
    private const string ClassNotificationProviderType = "ClassIsland.Services.NotificationProviders.ClassNotificationProvider";
    private object? _classNotificationProvider;
    private MethodInfo? _preparingDeltaMethod;
    private bool _preparingLookupFailed;

    public Subject? CurrentSubject => lessons.CurrentSubject;
    public Subject NextClassSubject => FollowingClass().Subject;
    public TimeState CurrentState => lessons.CurrentState;
    public TimeLayoutItem CurrentTimeLayoutItem => lessons.CurrentTimeLayoutItem;
    public TimeLayoutItem NextClassTimeLayoutItem => FollowingClass().Item;
    public ClassPlan? CurrentClassPlan => lessons.CurrentClassPlan;
    public bool IsClassPlanEnabled => lessons.IsClassPlanEnabled;
    public bool IsClassPlanLoaded => lessons.IsClassPlanLoaded;
    public TimeSpan OnClassLeftTime => lessons.OnClassLeftTime;
    public TimeSpan OnBreakingTimeLeftTime => lessons.OnBreakingTimeLeftTime;
    public bool IsLessonConfirmed => lessons.IsLessonConfirmed;

    public TimeSpan? ClassPreparingDuration
    {
        get
        {
            if (!ResolvePreparingDelta()) return null;
            try
            {
                return TimeSpan.FromSeconds((int)_preparingDeltaMethod!.Invoke(_classNotificationProvider, null)!);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// ClassIsland 的“下一节”取第一个结束时间未过的上课时段，上课期间它就是当堂课。
    /// 上课时改取当前时段结束后开始的第一节课，科目映射与 LessonsService 相同（按上课时段序号对应 Classes）。
    /// </summary>
    private (Subject Subject, TimeLayoutItem Item) FollowingClass()
    {
        if (lessons.CurrentState != TimeState.OnClass) return (lessons.NextClassSubject, lessons.NextClassTimeLayoutItem);
        var plan = lessons.CurrentClassPlan;
        var layout = plan?.TimeLayout?.Layouts;
        if (plan is null || layout is null) return (Subject.Fallback, TimeLayoutItem.Empty);
        var currentEnd = lessons.CurrentTimeLayoutItem.EndTime;
        var next = plan.ValidTimeLayoutItems.FirstOrDefault(i => i.TimeType == 0 && i.StartTime >= currentEnd);
        if (next is null) return (Subject.Fallback, TimeLayoutItem.Empty);
        var index = layout.Where(i => i.TimeType == 0).ToList().IndexOf(next);
        var subjects = HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profiles.Profile, "Subjects");
        return index >= 0 && plan.Classes.Count > index && subjects.TryGetValue(plan.Classes[index].SubjectId, out var subject)
            ? (subject, next)
            : (Subject.Fallback, next);
    }

    /// <summary>上课提醒提供方是宿主内部类型，按类型名从已注册的托管服务中找到它并反射其内部方法。</summary>
    private bool ResolvePreparingDelta()
    {
        if (_preparingDeltaMethod is not null) return true;
        if (_preparingLookupFailed) return false;
        try
        {
            _classNotificationProvider = services.GetServices<IHostedService>()
                .FirstOrDefault(s => s.GetType().FullName == ClassNotificationProviderType);
            _preparingDeltaMethod = _classNotificationProvider?.GetType().GetMethod(
                "GetSettingsDeltaTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        }
        catch
        {
            _preparingDeltaMethod = null;
        }
        _preparingLookupFailed = _preparingDeltaMethod is null;
        return !_preparingLookupFailed;
    }

    public event EventHandler? OnClass { add => lessons.OnClass += value; remove => lessons.OnClass -= value; }
    public event EventHandler? OnBreakingTime { add => lessons.OnBreakingTime += value; remove => lessons.OnBreakingTime -= value; }
    public event EventHandler? OnAfterSchool { add => lessons.OnAfterSchool += value; remove => lessons.OnAfterSchool -= value; }
    public event EventHandler? CurrentTimeStateChanged { add => lessons.CurrentTimeStateChanged += value; remove => lessons.CurrentTimeStateChanged -= value; }
    public event EventHandler? PostMainTimerTicked { add => lessons.PostMainTimerTicked += value; remove => lessons.PostMainTimerTicked -= value; }
}

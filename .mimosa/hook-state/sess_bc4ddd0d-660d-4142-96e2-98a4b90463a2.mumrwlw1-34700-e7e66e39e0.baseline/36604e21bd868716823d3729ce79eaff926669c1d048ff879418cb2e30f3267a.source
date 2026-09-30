using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;

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

    event EventHandler? OnClass;
    event EventHandler? OnBreakingTime;
    event EventHandler? OnAfterSchool;
    event EventHandler? CurrentTimeStateChanged;
    event EventHandler? PostMainTimerTicked;
}

public sealed class StateSourceAdapter(ILessonsService lessons) : IStateSource
{
    public Subject? CurrentSubject => lessons.CurrentSubject;
    public Subject NextClassSubject => lessons.NextClassSubject;
    public TimeState CurrentState => lessons.CurrentState;
    public TimeLayoutItem CurrentTimeLayoutItem => lessons.CurrentTimeLayoutItem;
    public TimeLayoutItem NextClassTimeLayoutItem => lessons.NextClassTimeLayoutItem;
    public ClassPlan? CurrentClassPlan => lessons.CurrentClassPlan;
    public bool IsClassPlanEnabled => lessons.IsClassPlanEnabled;
    public bool IsClassPlanLoaded => lessons.IsClassPlanLoaded;
    public TimeSpan OnClassLeftTime => lessons.OnClassLeftTime;
    public TimeSpan OnBreakingTimeLeftTime => lessons.OnBreakingTimeLeftTime;
    public bool IsLessonConfirmed => lessons.IsLessonConfirmed;

    public event EventHandler? OnClass { add => lessons.OnClass += value; remove => lessons.OnClass -= value; }
    public event EventHandler? OnBreakingTime { add => lessons.OnBreakingTime += value; remove => lessons.OnBreakingTime -= value; }
    public event EventHandler? OnAfterSchool { add => lessons.OnAfterSchool += value; remove => lessons.OnAfterSchool -= value; }
    public event EventHandler? CurrentTimeStateChanged { add => lessons.CurrentTimeStateChanged += value; remove => lessons.CurrentTimeStateChanged -= value; }
    public event EventHandler? PostMainTimerTicked { add => lessons.PostMainTimerTicked += value; remove => lessons.PostMainTimerTicked -= value; }
}

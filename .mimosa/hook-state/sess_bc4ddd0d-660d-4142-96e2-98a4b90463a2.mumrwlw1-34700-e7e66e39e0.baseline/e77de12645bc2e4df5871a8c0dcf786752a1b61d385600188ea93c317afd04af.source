using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class StateCollectorTests
{
    private sealed class FakeStateSource : IStateSource
    {
        public Subject? CurrentSubject { get; set; }
        public Subject NextClassSubject { get; set; } = new();
        public TimeState CurrentState { get; set; }
        public TimeLayoutItem CurrentTimeLayoutItem { get; set; } = TimeLayoutItem.Empty;
        public TimeLayoutItem NextClassTimeLayoutItem { get; set; } = TimeLayoutItem.Empty;
        public ClassPlan? CurrentClassPlan { get; set; }
        public bool IsClassPlanEnabled { get; set; }
        public bool IsClassPlanLoaded { get; set; }
        public TimeSpan OnClassLeftTime { get; set; }
        public TimeSpan OnBreakingTimeLeftTime { get; set; }
        public bool IsLessonConfirmed { get; set; }

        public event EventHandler? OnClass;
        public event EventHandler? OnBreakingTime;
        public event EventHandler? OnAfterSchool;
        public event EventHandler? CurrentTimeStateChanged;
        public event EventHandler? PostMainTimerTicked;

        public void RaiseOnClass() => OnClass?.Invoke(this, EventArgs.Empty);
        public void RaiseOnBreakingTime() => OnBreakingTime?.Invoke(this, EventArgs.Empty);
        public void RaiseOnAfterSchool() => OnAfterSchool?.Invoke(this, EventArgs.Empty);
        public void RaiseCurrentTimeStateChanged() => CurrentTimeStateChanged?.Invoke(this, EventArgs.Empty);
        public void RaisePostMainTimerTicked() => PostMainTimerTicked?.Invoke(this, EventArgs.Empty);
    }

    private sealed class EmptyBackend : IScheduleBackend
    {
        public IReadOnlyDictionary<Guid, Subject> Subjects { get; } = new Dictionary<Guid, Subject>();
        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            planId = null;
            return null;
        }
    }

    private static StateCollector CreateCollector(FakeStateSource source)
    {
        // 宿主控制服务以空依赖构造：测试环境中不加载 ClassIsland 宿主程序集，
        // 反射路径自然回落为“不支持”，通知播放/主界面可见性取默认值。
        var hostControl = new ClassIslandHostControlService(
            null, null, NullLogger<ClassIslandHostControlService>.Instance);
        return new StateCollector(
            source, new ScheduleCatalog(new EmptyBackend()), hostControl, NullLogger<StateCollector>.Instance);
    }

    [Fact]
    public void BuildSnapshot_MapsAllStateSourceFieldsToProtocolSnapshot()
    {
        var source = new FakeStateSource
        {
            CurrentSubject = new Subject { Name = "语文" },
            NextClassSubject = new Subject { Name = "数学" },
            CurrentState = TimeState.OnClass,
            CurrentTimeLayoutItem = new TimeLayoutItem
            {
                StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromHours(9), TimeType = 0,
            },
            NextClassTimeLayoutItem = new TimeLayoutItem
            {
                StartTime = TimeSpan.FromHours(9),
                EndTime = TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(10)),
                TimeType = 1,
                BreakName = "课间",
            },
            CurrentClassPlan = new ClassPlan { Name = "主课表" },
            IsClassPlanEnabled = true,
            IsClassPlanLoaded = true,
            OnClassLeftTime = TimeSpan.FromMinutes(12),
            OnBreakingTimeLeftTime = TimeSpan.FromMinutes(3),
            IsLessonConfirmed = true,
        };

        var snapshot = CreateCollector(source).BuildSnapshot();

        Assert.Equal("语文", snapshot.CurrentSubject);
        Assert.Equal("数学", snapshot.NextClassSubject);
        Assert.Equal(ClassStateKind.Class, snapshot.CurrentState);
        Assert.Equal("08:00-09:00 语文", snapshot.CurrentTimeLayoutItem);
        Assert.Equal("09:00-09:10 课间", snapshot.NextClassTimeLayoutItem);
        Assert.Equal("主课表", snapshot.ClassPlanName);
        Assert.True(snapshot.IsClassPlanEnabled);
        Assert.True(snapshot.IsClassPlanLoaded);
        Assert.Equal(TimeSpan.FromMinutes(12), snapshot.OnClassLeftTime);
        Assert.Equal(TimeSpan.FromMinutes(3), snapshot.OnBreakingLeftTime);
        Assert.True(snapshot.LessonConfirmed);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), snapshot.ScheduleDate);
        Assert.Equal(
            (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
            snapshot.TimeZoneOffsetMinutes);
        Assert.InRange(snapshot.GeneratedAt, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        // 宿主服务未注入：通知播放状态回落为 false，主界面可见性回落为 true。
        Assert.False(snapshot.IsNotificationPlaying);
        Assert.True(snapshot.IsMainMenuVisible);
    }

    [Fact]
    public void Start_PushesInitialSnapshotAndScheduleThenForwardsOnClassEvent()
    {
        var source = new FakeStateSource { CurrentSubject = new Subject { Name = "英语" } };
        var collector = CreateCollector(source);
        var snapshots = new List<ClassStateSnapshot>();
        var schedules = new List<ScheduleBundle>();
        var events = new List<ClassEvent>();
        collector.SnapshotPushed += snapshots.Add;
        collector.SchedulePushed += schedules.Add;
        collector.EventOccurred += events.Add;

        collector.Start();

        Assert.Single(snapshots);
        Assert.Single(schedules);
        Assert.Empty(events);

        source.RaiseOnClass();

        var evt = Assert.Single(events);
        Assert.Equal(ClassEventKind.OnClass, evt.Event);
        Assert.Equal("英语", evt.Subject);
        Assert.Contains("上课了", evt.Message);
        Assert.Equal(2, snapshots.Count); // 事件后随推一次快照。
    }

    [Theory]
    [InlineData(ClassEventKind.OnBreaking, "下课休息")]
    [InlineData(ClassEventKind.OnAfterSchool, "放学啦！")]
    public void Start_ForwardsBreakingAndAfterSchoolEvents(ClassEventKind kind, string message)
    {
        var source = new FakeStateSource();
        var collector = CreateCollector(source);
        var events = new List<ClassEvent>();
        collector.EventOccurred += events.Add;
        collector.Start();

        if (kind == ClassEventKind.OnBreaking) source.RaiseOnBreakingTime();
        else source.RaiseOnAfterSchool();

        var evt = Assert.Single(events);
        Assert.Equal(kind, evt.Event);
        Assert.Equal(message, evt.Message);
    }

    [Fact]
    public void Stop_UnsubscribesFromStateSourceEvents()
    {
        var source = new FakeStateSource();
        var collector = CreateCollector(source);
        var events = new List<ClassEvent>();
        collector.EventOccurred += events.Add;
        collector.Start();
        collector.Stop();

        source.RaiseOnClass();
        source.RaiseOnBreakingTime();
        source.RaiseOnAfterSchool();

        Assert.Empty(events);
    }

    [Fact]
    public void CurrentTimeStateChanged_PushesSnapshotImmediately()
    {
        var source = new FakeStateSource();
        var collector = CreateCollector(source);
        var snapshots = new List<ClassStateSnapshot>();
        collector.SnapshotPushed += snapshots.Add;
        collector.Start();
        var baseline = snapshots.Count;

        source.RaiseCurrentTimeStateChanged();

        Assert.Equal(baseline + 1, snapshots.Count);
    }

    [Fact]
    public async Task PostMainTimerTicked_ThrottlesSnapshotToOncePerSecond()
    {
        var source = new FakeStateSource();
        var collector = CreateCollector(source);
        var snapshots = new List<ClassStateSnapshot>();
        collector.SnapshotPushed += snapshots.Add;
        collector.Start();
        var baseline = snapshots.Count;

        // 等待节流窗口过去，确保第一次触发必然推送。
        await Task.Delay(1100);
        source.RaisePostMainTimerTicked();
        Assert.Equal(baseline + 1, snapshots.Count);
        // 1 秒内的第二次触发被节流，不产生新快照。
        source.RaisePostMainTimerTicked();
        Assert.Equal(baseline + 1, snapshots.Count);
    }

    [Fact]
    public void ForceSchedulePush_EmitsScheduleChangedEventWithSnapshotAndBundle()
    {
        var collector = CreateCollector(new FakeStateSource());
        var events = new List<ClassEvent>();
        var schedules = new List<ScheduleBundle>();
        var snapshots = new List<ClassStateSnapshot>();
        collector.EventOccurred += events.Add;
        collector.SchedulePushed += schedules.Add;
        collector.SnapshotPushed += snapshots.Add;

        collector.ForceSchedulePush();

        var evt = Assert.Single(events);
        Assert.Equal(ClassEventKind.ScheduleChanged, evt.Event);
        Assert.Equal("课表已更新", evt.Message);
        Assert.Single(schedules);
        Assert.Single(snapshots);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("???", null)]
    [InlineData("语文", "语文")]
    public void SubjectName_NormalizesMissingAndPlaceholderNames(string? name, string? expected)
    {
        var subject = name is null ? null : new Subject { Name = name };

        Assert.Equal(expected, StateCollector.SubjectName(subject));
    }
}

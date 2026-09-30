using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SchedulePipelineTests
{
    private sealed class FakeBackend : IScheduleBackend
    {
        public Dictionary<Guid, Subject> Subjects { get; } = [];
        public ClassPlan? Plan { get; set; }
        public Guid? PlanId { get; set; }
        IReadOnlyDictionary<Guid, Subject> IScheduleBackend.Subjects => Subjects;
        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            planId = PlanId;
            return Plan;
        }
    }

    [Fact]
    public void BuildDay_RevisionIsDeterministicAndChangesWithCourseSubject()
    {
        var subjectId = Guid.NewGuid();
        var backend = new FakeBackend
        {
            PlanId = Guid.NewGuid(),
            Plan = new ClassPlan
            {
                Name = "主课表",
                Classes = { new ClassInfo { SubjectId = subjectId, IsEnabled = true } },
            },
        };
        backend.Subjects[subjectId] = new Subject { Name = "语文" };
        var catalog = new ScheduleCatalog(backend);
        var day = new DateTime(2026, 8, 14);

        var first = catalog.BuildDay(day);
        var second = catalog.BuildDay(day);

        Assert.Equal(first.Revision, second.Revision);
        Assert.Single(first.Courses);
        Assert.Equal("语文", first.Courses[0].Subject);
        Assert.True(first.Enabled);
        Assert.Equal("主课表", first.ClassPlanName);

        backend.Plan.Classes[0].SubjectId = Guid.NewGuid();
        Assert.NotEqual(first.Revision, catalog.BuildDay(day).Revision);
    }

    [Fact]
    public void BuildBundle_ListsSevenDaysAndMarksMissingPlanAsDisabled()
    {
        var backend = new FakeBackend(); // 未设置课表。
        var catalog = new ScheduleCatalog(backend);

        var bundle = catalog.BuildBundle(new DateTime(2026, 8, 14));

        Assert.Equal(7, bundle.Days.Count);
        Assert.All(bundle.Days, scheduleDay => Assert.False(scheduleDay.Enabled));
        Assert.Empty(bundle.Subjects);
    }

    [Fact]
    public void BuildScheduleSignature_ChangesOnSubjectRenameAndPlanRename()
    {
        var bundle = new ScheduleBundle
        {
            Days = [new ScheduleDay { Date = "2026-08-14", Revision = "rev-1", ClassPlanName = "主课表" }],
            Subjects = [new SubjectEntry { Id = Guid.NewGuid(), Name = "语文" }],
        };

        var baseline = StateCollector.BuildScheduleSignature(bundle);

        bundle.Subjects[0].Name = "语文（新）";
        Assert.NotEqual(baseline, StateCollector.BuildScheduleSignature(bundle));

        bundle.Subjects[0].Name = "语文";
        bundle.Days[0].ClassPlanName = "临时课表";
        Assert.NotEqual(baseline, StateCollector.BuildScheduleSignature(bundle));
    }

    [Fact]
    public void FormatTimeLayoutItem_HandlesEmptyCourseBreakAndUnknownTypes()
    {
        Assert.Equal(string.Empty, StateCollector.FormatTimeLayoutItem(TimeLayoutItem.Empty, "语文"));

        var course = new TimeLayoutItem
        {
            StartTime = TimeSpan.FromHours(8),
            EndTime = TimeSpan.FromHours(9),
            TimeType = 0,
        };
        Assert.Equal("08:00-09:00 语文", StateCollector.FormatTimeLayoutItem(course, "语文"));
        Assert.Equal("08:00-09:00", StateCollector.FormatTimeLayoutItem(course, null));

        var breakItem = new TimeLayoutItem
        {
            StartTime = TimeSpan.FromHours(9),
            EndTime = TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(10)),
            TimeType = 1,
            BreakName = "课间",
        };
        Assert.Equal("09:00-09:10 课间", StateCollector.FormatTimeLayoutItem(breakItem, null));
    }

    [Theory]
    [InlineData(TimeState.OnClass, ClassStateKind.Class)]
    [InlineData(TimeState.Breaking, ClassStateKind.Breaking)]
    [InlineData(TimeState.AfterSchool, ClassStateKind.AfterSchool)]
    [InlineData(TimeState.PrepareOnClass, ClassStateKind.PrepareClass)]
    public void MapState_MapsClassIslandStatesToProtocolKinds(TimeState input, ClassStateKind expected) =>
        Assert.Equal(expected, StateCollector.MapState(input));

    [Fact]
    public void MapState_UnknownStateFallsBackToNone() =>
        Assert.Equal(ClassStateKind.None, StateCollector.MapState((TimeState)999));

    [Fact]
    public void ValidateScheduleChangeRequest_RejectsNullMalformedOutOfRangeAndMissingRevision()
    {
        var today = new DateTime(2026, 8, 14);

        Assert.Equal("换课日期无效", CommandHandler.ValidateScheduleChangeRequest(null, today, out _));
        Assert.Equal("换课日期无效", CommandHandler.ValidateScheduleChangeRequest(
            new ScheduleChangeRequest { Date = "2026/08/14", ExpectedRevision = "rev" }, today, out _));
        Assert.Equal("只能修改今天起未来七天的课表", CommandHandler.ValidateScheduleChangeRequest(
            new ScheduleChangeRequest { Date = "2026-08-13", ExpectedRevision = "rev" }, today, out _));
        Assert.Equal("只能修改今天起未来七天的课表", CommandHandler.ValidateScheduleChangeRequest(
            new ScheduleChangeRequest { Date = "2026-08-21", ExpectedRevision = "rev" }, today, out _));
        Assert.Equal("缺少课表修订号", CommandHandler.ValidateScheduleChangeRequest(
            new ScheduleChangeRequest { Date = "2026-08-14" }, today, out _));
    }

    [Fact]
    public void ValidateScheduleChangeRequest_AcceptsTodayAndNextSixDays()
    {
        var today = new DateTime(2026, 8, 14);
        var request = new ScheduleChangeRequest { Date = "2026-08-20", ExpectedRevision = "rev" };

        var error = CommandHandler.ValidateScheduleChangeRequest(request, today, out var date);

        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 8, 20), date);
    }
}

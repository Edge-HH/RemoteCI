using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SubjectTeacherExecutorTests
{
    private sealed class FakeProfileOps : IProfileWriteOperations
    {
        public Dictionary<Guid, Subject> Subjects { get; } = [];
        public Dictionary<Guid, ClassPlan> ClassPlans { get; } = [];
        public Guid? NextOverlayId { get; set; }
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }
        IReadOnlyDictionary<Guid, Subject> IProfileWriteOperations.Subjects => Subjects;
        IReadOnlyDictionary<Guid, ClassPlan> IProfileWriteOperations.ClassPlans => ClassPlans;
        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime? enableDateTime = null) => NextOverlayId;
        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }
    }

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

    private static SubjectTeacherRequest Request(Guid subjectId, string? teacherName) =>
        new() { SubjectId = subjectId, TeacherName = teacherName };

    [Fact]
    public void Validate_RejectsMissingSubjectAndOverlongName()
    {
        Assert.NotNull(SubjectTeacherExecutor.Validate(null));
        Assert.NotNull(SubjectTeacherExecutor.Validate(Request(Guid.Empty, "王老师")));
        Assert.NotNull(SubjectTeacherExecutor.Validate(
            Request(Guid.NewGuid(), new string('师', SubjectTeacherRequest.MaxTeacherNameLength + 1))));
        Assert.Null(SubjectTeacherExecutor.Validate(Request(Guid.NewGuid(), "王老师")));
        Assert.Null(SubjectTeacherExecutor.Validate(Request(Guid.NewGuid(), null)));
    }

    [Fact]
    public void Apply_SetsTrimsAndClearsTeacherNameAndSavesProfile()
    {
        var profile = new FakeProfileOps();
        var subjectId = Guid.NewGuid();
        profile.Subjects[subjectId] = new Subject { Name = "数学", TeacherName = "旧教师" };

        var set = SubjectTeacherExecutor.Apply(Request(subjectId, "  王老师  "), profile);
        Assert.True(set.Success);
        Assert.Equal("王老师", profile.Subjects[subjectId].TeacherName);
        Assert.Equal(1, profile.SaveCount);

        var clear = SubjectTeacherExecutor.Apply(Request(subjectId, ""), profile);
        Assert.True(clear.Success);
        Assert.Equal(string.Empty, profile.Subjects[subjectId].TeacherName);
    }

    [Fact]
    public void Apply_SameNameSkipsSaveAndUnknownSubjectFails()
    {
        var profile = new FakeProfileOps();
        var subjectId = Guid.NewGuid();
        profile.Subjects[subjectId] = new Subject { Name = "数学", TeacherName = "王老师" };

        var unchanged = SubjectTeacherExecutor.Apply(Request(subjectId, "王老师"), profile);
        Assert.True(unchanged.Success);
        Assert.Equal(0, profile.SaveCount);

        var missing = SubjectTeacherExecutor.Apply(Request(Guid.NewGuid(), "王老师"), profile);
        Assert.False(missing.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, missing.Code);
    }

    [Fact]
    public void Apply_SaveFailureRollsBackTeacherName()
    {
        var profile = new FakeProfileOps { ThrowOnSave = true };
        var subjectId = Guid.NewGuid();
        profile.Subjects[subjectId] = new Subject { Name = "数学", TeacherName = "旧教师" };

        var result = SubjectTeacherExecutor.Apply(Request(subjectId, "王老师"), profile);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.SaveFailed, result.Code);
        Assert.Equal("旧教师", profile.Subjects[subjectId].TeacherName);
    }

    [Fact]
    public void Catalog_IncludesTeacherFromSubject()
    {
        var backend = new FakeBackend();
        var subjectId = Guid.NewGuid();
        var subject = new Subject { Name = "数学", TeacherName = " 王老师 " };
        backend.Subjects[subjectId] = subject;
        backend.PlanId = Guid.NewGuid();
        backend.Plan = new ClassPlan
        {
            Name = "主课表",
            Classes = [new ClassInfo { SubjectId = subjectId, IsEnabled = true }],
        };
        var catalog = new ScheduleCatalog(backend);

        var day = catalog.BuildDay(new DateTime(2026, 9, 29));
        var course = day.Courses.Single();
        Assert.Equal("王老师", course.Teacher);
        var bundle = catalog.BuildBundle(new DateTime(2026, 9, 29));
        Assert.Equal("王老师", bundle.Subjects.Single(x => x.Id == subjectId).Teacher);

        subject.TeacherName = " ";
        var withoutTeacher = catalog.BuildDay(new DateTime(2026, 9, 29));
        Assert.Null(withoutTeacher.Courses.Single().Teacher);
    }
}

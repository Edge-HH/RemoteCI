using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class TeacherClashDetectorTests
{
    private const string Date = "2026-10-26";
    private static readonly Guid ClassA = Guid.NewGuid();
    private static readonly Guid ClassB = Guid.NewGuid();

    [Fact]
    public void SameIndexInDifferentTimeLayouts_DoesNotClash()
    {
        // 乙班第 1 节 08:10 开始、甲班第 1 节 07:20 结束：同为第 1 节但时间不重叠。
        var schedules = Schedules(
            (ClassA, [("语文", "李老师", "06:40", "07:20")]),
            (ClassB, [("数学", "王老师", "08:10", "08:55")]));

        var clashes = TeacherClashDetector.Find(schedules, [new ProposedLesson(ClassA, Date, 0, "王老师", "06:40", "07:20")]);

        Assert.Empty(clashes);
    }

    [Fact]
    public void OverlappingTimeInAnotherClass_Clashes()
    {
        var schedules = Schedules(
            (ClassA, [("语文", "李老师", "08:00", "08:45")]),
            (ClassB, [("数学", "王老师", "08:30", "09:15")]));

        var clash = Assert.Single(TeacherClashDetector.Find(schedules, [new ProposedLesson(ClassA, Date, 0, "王老师", "08:00", "08:45")]));

        Assert.Equal("王老师", clash.Teacher);
        Assert.Equal(ClassB, clash.ClassId);
        Assert.Equal("数学", clash.Course.Subject);
    }

    [Fact]
    public void LessonsVacatedBySameSwap_AreIgnored()
    {
        // 跨班同一时间互换：王老师离开乙班第 1 节去甲班，李老师反之；腾出的课位不算撞课。
        var schedules = Schedules(
            (ClassA, [("语文", "李老师", "08:00", "08:45")]),
            (ClassB, [("数学", "王老师", "08:00", "08:45")]));

        var clashes = TeacherClashDetector.Find(schedules,
        [
            new ProposedLesson(ClassA, Date, 0, "王老师", "08:00", "08:45"),
            new ProposedLesson(ClassB, Date, 0, "李老师", "08:00", "08:45"),
        ]);

        Assert.Empty(clashes);
    }

    [Fact]
    public void MultiTeacherFields_AndMissingTimes_AreHandled()
    {
        var schedules = Schedules(
            (ClassA, [("语文", "李老师", null, null)]),
            (ClassB, [("实验", "王老师/赵老师", "08:00", "08:45"), ("自习", "赵老师", null, null)]));

        var clashes = TeacherClashDetector.Find(schedules, [new ProposedLesson(ClassA, Date, 0, "赵老师", "08:15", "08:40")]);

        Assert.Equal("实验", Assert.Single(clashes).Course.Subject);
        Assert.Empty(TeacherClashDetector.Find(schedules, [new ProposedLesson(ClassA, Date, 0, "赵老师", null, null)]));
    }

    private static Dictionary<Guid, ScheduleBundle> Schedules(
        params (Guid ClassId, (string Subject, string Teacher, string? Start, string? End)[] Lessons)[] classes) =>
        classes.ToDictionary(x => x.ClassId, x => new ScheduleBundle
        {
            FromDate = Date,
            ClassId = x.ClassId,
            Days =
            [
                new ScheduleDay
                {
                    Date = Date,
                    Enabled = true,
                    Courses = x.Lessons.Select((lesson, index) => new CourseEntry
                    {
                        Index = index,
                        Label = $"第{index + 1}节",
                        SubjectId = Guid.NewGuid(),
                        Subject = lesson.Subject,
                        Teacher = lesson.Teacher,
                        StartTime = lesson.Start,
                        EndTime = lesson.End,
                        Enabled = true,
                    }).ToList(),
                },
            ],
        });
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>课时台账：课结束后才记录、只记一次，换课产生的临时任课老师记为代课并计入统计。</summary>
public sealed class LessonLedgerTests
{
    private static readonly Guid ClassId = TestWebApplicationFactory.DefaultClassId;
    private const string Date = "2030-01-07";
    private static readonly DateTimeOffset Morning = new(2030, 1, 7, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task EndedLessonsAreRecordedOnce_WithSubstituteTeachersAndStats()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        var state = factory.Services.GetRequiredService<IStateStore>();
        state.SaveSnapshot(ClassId, new ClassStateSnapshot { ClassId = ClassId, TimeZoneOffsetMinutes = 480 });
        state.SaveSchedule(ClassId, new ScheduleBundle
        {
            ClassId = ClassId,
            FromDate = Date,
            Days =
            [
                new ScheduleDay
                {
                    Date = Date,
                    Enabled = true,
                    Courses =
                    [
                        new CourseEntry { Index = 0, Label = "第1节", Subject = "语文", Teacher = "李老师", StartTime = "08:00", EndTime = "08:45", Enabled = true },
                        new CourseEntry { Index = 1, Label = "第2节", Subject = "数学", Teacher = "王老师/赵老师", StartTime = "09:00", EndTime = "09:45", Enabled = true },
                        new CourseEntry { Index = 2, Label = "第3节", Subject = "", StartTime = "10:00", EndTime = "10:45", Enabled = true },
                    ],
                },
            ],
        });
        // 换课申请让“代课老师”临时上第 2 节数学。
        factory.Services.GetRequiredService<LessonOverrideTable>().Set(new LessonOverride(ClassId, Date, 1, "代课老师", "数学"));

        Assert.Equal(1, await RecordAsync(factory, Morning.AddMinutes(50)));
        Assert.Equal(1, await RecordAsync(factory, Morning.AddHours(3)));
        Assert.Equal(0, await RecordAsync(factory, Morning.AddHours(4)));

        using (var scope = factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().LessonLedgerEntries
                .AsNoTracking().OrderBy(x => x.Index).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.False(rows[0].IsSubstitute);
            Assert.True(rows[1].IsSubstitute);
            Assert.Equal("代课老师", rows[1].TeacherName);
            Assert.Equal("王老师/赵老师", rows[1].DefaultTeacherName);
        }

        using var statsScope = factory.Services.CreateScope();
        var report = await statsScope.ServiceProvider.GetRequiredService<LessonStatsService>()
            .GetAsync(null, new DateOnly(2030, 1, 7), new DateOnly(2030, 1, 7));
        var byName = report.Teachers.ToDictionary(x => x.Teacher);
        Assert.Equal(2, report.LessonCount);
        Assert.Equal((1, 0, 0), (byName["李老师"].Total, byName["李老师"].Substitute, byName["李老师"].Substituted));
        Assert.Equal((1, 1, 0), (byName["代课老师"].Total, byName["代课老师"].Substitute, byName["代课老师"].Substituted));
        Assert.Equal((0, 0, 1), (byName["王老师"].Total, byName["王老师"].Substitute, byName["王老师"].Substituted));
        Assert.Equal(1, byName["赵老师"].Substituted);

        var mine = await statsScope.ServiceProvider.GetRequiredService<LessonStatsService>()
            .GetAsync(null, new DateOnly(2030, 1, 7), new DateOnly(2030, 1, 7), teacher: "李老师");
        Assert.Equal("李老师", Assert.Single(mine.Teachers).Teacher);
        var excel = await statsScope.ServiceProvider.GetRequiredService<LessonStatsService>()
            .ExportAsync(null, new DateOnly(2030, 1, 7), new DateOnly(2030, 1, 7));
        Assert.True(excel.Length > 0);
    }

    private static async Task<int> RecordAsync(TestWebApplicationFactory factory, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LessonLedgerRecorder>().RecordAsync(now.ToUniversalTime());
    }
}

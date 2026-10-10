using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ClassStateCacheTests
{
    [Fact]
    public async Task ServerRestart_KeepsLastSyncedScheduleAndExtensionSettingsWhilePluginOffline()
    {
        // 同一个数据库文件模拟服务端重启：插件离线期间仍能读取最后一次同步的课表与扩展设置。
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "state-cache.db");
        Guid classId;
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            var admin = await first.LoginAsync();
            var created = await first.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
                HttpMethod.Post, "/api/classes", admin.AccessToken, new CreateClassRequest { Name = "缓存测试班" }));
            created.EnsureSuccessStatusCode();
            classId = (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;

            var store = first.Services.GetRequiredService<IStateStore>();
            store.SaveSchedule(classId, new ScheduleBundle
            {
                FromDate = "2026-10-12",
                Days =
                [
                    new ScheduleDay
                    {
                        Date = "2026-10-12", Revision = "r1", Enabled = true,
                        Courses = [new CourseEntry { Index = 0, Subject = "数学", Teacher = "王老师", Enabled = true }],
                    },
                ],
            });
            store.SaveExtensions(classId, [new ExtensionDefinition { Id = "demo.lock", DisplayName = "锁屏", GroupId = "demo.settings" }]);
            store.SaveExtensionGroups(classId,
            [
                new ExtensionGroupDefinition
                {
                    Id = "demo.settings",
                    DisplayName = "演示插件",
                    Settings = [new ExtensionParameter { Key = "volume", Label = "音量", Type = ExtensionParameterType.Number }],
                    Values = new Dictionary<string, string?> { ["volume"] = "40" },
                },
            ]);
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        var secondAdmin = await second.LoginAsync();
        var schedule = await second.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, $"/api/schedule?classId={classId}", secondAdmin.AccessToken));
        Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        var bundle = (await schedule.Content.ReadFromJsonAsync<ScheduleBundle>())!;
        Assert.Equal("数学", Assert.Single(Assert.Single(bundle.Days).Courses).Subject);

        var restored = second.Services.GetRequiredService<IStateStore>();
        Assert.Equal("demo.lock", Assert.Single(restored.GetLatestExtensions(classId)!).Id);
        var group = Assert.Single(restored.GetLatestExtensionGroups(classId)!);
        Assert.Equal("40", group.Values!["volume"]);
    }
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>档案版本历史、语义差异与恢复。</summary>
public sealed class ProfileHistoryTests
{
    private const string MathId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    [Fact]
    public void Diff_DescribesLessonTeacherAndTimeChanges()
    {
        var before = ProfileTestData.Json();
        var after = Edit(before, profile =>
        {
            profile["Subjects"]![MathId] = new JsonObject { ["Name"] = "数学", ["TeacherName"] = "王老师" };
            profile["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"] = "新教师";
            profile["ClassPlans"]![ProfileTestData.PlanId]!["Classes"]![0]!["SubjectId"] = MathId;
            var point = profile["TimeLayouts"]![ProfileTestData.LayoutId]!["Layouts"]![0]!;
            point["StartTime"] = "08:05:00";
            point["EndTime"] = "08:45:00";
        });

        var texts = ProfileDocument.Diff(before, after).Select(x => x.Text).ToList();

        Assert.Contains("新增科目「数学」", texts);
        Assert.Contains("「语文」任课教师 原教师 → 新教师", texts);
        Assert.Contains("时间表「常规时间表」第 1 节 上课 08:00–08:40 → 上课 08:05–08:45", texts);
        Assert.Contains("课表「周一课表」第 1 节 语文 → 数学", texts);
        Assert.Empty(ProfileDocument.Diff(before, before));
    }

    [Fact]
    public void Diff_MatchesByNameWhenDeviceIdsDiffer_AndCanSkipTempLayers()
    {
        var server = ProfileTestData.Json();
        // 设备档案来自别处导入：对象 ID 全部不同，但名称相同、内容一致，只多了一个临时层。
        var device = ProfileDocument.Parse(ProfileTestData.WithTempLayer(server, "2030-01-07"));
        var renamed = ProfileDocument.Serialize(device)
            .Replace(ProfileTestData.SubjectId, "11111111-2222-3333-4444-555555555555")
            .Replace(ProfileTestData.LayoutId, "66666666-7777-8888-9999-000000000000");

        Assert.Empty(ProfileDocument.Diff(server, renamed, includeTempLayers: false));
        Assert.Contains(ProfileDocument.Diff(server, renamed), x => x.Category == "临时层");
    }

    [Fact]
    public async Task Library_RecordsVersions_DiffsThem_AndRestoresAsNewRevision()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var first = (await library.SaveAsync(admin, [ProfileTestData.New("历史模板")]))[0];
        var edited = Edit(first.ProfileJson, profile => profile["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"] = "第二版教师");
        var second = (await library.SaveAsync(admin, [ProfileTestData.Edit(first, json: edited)]))[0];

        var versions = await library.ListVersionsAsync(admin, first.Id);
        Assert.Equal([second.Revision, first.Revision], versions.Select(x => x.Revision));
        Assert.True(versions[0].IsCurrent);
        Assert.Equal(1, versions[0].ChangeCount);

        var diff = await library.DiffVersionAsync(admin, first.Id, second.Revision, againstCurrent: false);
        Assert.Equal("「语文」任课教师 原教师 → 第二版教师", Assert.Single(diff.Changes).Text);

        var restored = await library.RestoreVersionAsync(admin, first.Id, first.Revision, second.Revision);
        Assert.Equal(second.Revision + 1, restored.Revision);
        Assert.Empty(ProfileDocument.Diff(first.ProfileJson, restored.ProfileJson));
        var after = await library.ListVersionsAsync(admin, first.Id);
        Assert.Equal(3, after.Count);
        Assert.Equal("恢复", after[0].OriginLabel);

        // 用过期的修订号恢复会被拒绝，不覆盖别人刚保存的内容。
        await Assert.ThrowsAsync<ProfileRevisionException>(() =>
            library.RestoreVersionAsync(admin, first.Id, first.Revision, second.Revision));
    }

    [Fact]
    public async Task DriftCheck_ComparesDeviceProfileWithClassProfile()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var classId = TestWebApplicationFactory.DefaultClassId;
        await library.SaveAsync(admin, [ProfileTestData.New("本班档案", classId)]);
        var deviceJson = Edit(ProfileTestData.Json(), profile =>
            profile["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"] = "本地改过的教师");

        var token = await factory.GetPluginTokenAsync();
        using var plugin = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{RemoteCI.Shared.Protocol.QueryToken}={Uri.EscapeDataString(token)}"), CancellationToken.None);
        await TestSockets.SendAsync(plugin, RemoteCI.Shared.Models.Envelope.PeerCapabilities(
            new PeerCapabilities { Capabilities = RemoteCI.Shared.RemoteCiCapabilities.Current }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!peers.PrimaryPluginSupports(classId, RemoteCI.Shared.RemoteCiCapabilities.ProfileRead))
                await Task.Delay(20, timeout.Token);

        var check = scope.ServiceProvider.GetRequiredService<ProfileDriftService>().CheckAsync(admin, [classId]);
        var command = await TestSockets.ReceiveAsync(plugin, RemoteCI.Shared.Protocol.MessageTypeCommand);
        await TestSockets.SendAsync(plugin, new Envelope
        {
            Type = RemoteCI.Shared.Protocol.MessageTypeCommandResult,
            ReplyToMessageId = command.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Data = deviceJson },
        });
        var result = Assert.Single(await check);

        Assert.True(result.Success);
        Assert.Equal(1, result.ChangeCount);
        var latest = await scope.ServiceProvider.GetRequiredService<ProfileDriftService>().LatestAsync();
        Assert.Equal("「语文」任课教师 原教师 → 本地改过的教师", Assert.Single(latest[classId].Changes).Text);
        plugin.Abort();
    }

    private static string Edit(string json, Action<JsonObject> change)
    {
        var profile = ProfileDocument.Parse(json);
        change(profile);
        return ProfileDocument.Serialize(profile);
    }
}

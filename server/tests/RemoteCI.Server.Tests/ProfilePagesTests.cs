using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ProfilePagesTests
{
    [Fact]
    public async Task DedicatedPagesRenderSafeBootstrapAndNavigationWithoutOldControlCards()
    {
        await using var factory = new TestWebApplicationFactory();
        const string maliciousName = "</script><img src=x onerror=alert(1)>";
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(scope.ServiceProvider), [ProfileTestData.New(maliciousName)]);
        }
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);

        var html = await browser.GetStringAsync("/Profiles");
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains("href=\"/Profiles\"", decoded);
        Assert.Contains("href=\"/ClassProfiles\"", decoded);
        Assert.Contains("data-profile-mode=\"library\"", decoded);
        Assert.Contains("data-profile-mode=\"classes\"", decoded);
        Assert.Contains("data-profile-app", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.DoesNotContain(maliciousName, html);
        var bootstrap = ReadBootstrap(html);
        Assert.True(bootstrap["isAdmin"]!.GetValue<bool>());
        Assert.Equal(maliciousName, bootstrap["profiles"]![0]!["name"]!.GetValue<string>());
        // 临时层过期判断使用教室端日期：没有状态快照时按服务端本地时区。
        Assert.Equal(ClassClock.Today(factory.Services.GetRequiredService<IStateStore>(), Classroom.DefaultId).ToString("yyyy-MM-dd"),
            bootstrap["classToday"]![Classroom.DefaultId.ToString()]!.GetValue<string>());

        var classPage = ReadBootstrap(await browser.GetStringAsync("/ClassProfiles"));
        Assert.Single(classPage["classes"]!.AsArray());
        Assert.Empty(classPage["profiles"]!.AsArray());
        foreach (var path in new[] { "/Control", "/BatchControl" })
        {
            var control = await browser.GetStringAsync(path);
            Assert.DoesNotContain("data-batch-open=\"UpdateTimeLayout\"", control);
            Assert.DoesNotContain("data-batch-open=\"DistributeProfile\"", control);
            Assert.DoesNotContain("batch-settings-time-layout", control);
            Assert.DoesNotContain("batch-settings-profile", control);
            Assert.Contains("data-batch-open=\"JoinManagement\"", control);
            // 远程插件管理策略的入口已移除，协议命令仅为旧客户端保留。
            Assert.DoesNotContain("data-batch-open=\"SetPluginManagementPolicy\"", control);
        }
    }

    [Fact]
    public async Task JsonHandlersRequireCsrfAndExportKeepsCompleteProfile()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var input = new { items = new[] { ProfileTestData.New("页面档案") } };
        var missingCsrf = await browser.PostAsJsonAsync("/Profiles?handler=Save", input);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var preview = await PostJsonAsync(browser, "/Profiles?handler=Preview", html,
            new { profileJson = ProfileTestData.Json() });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var parsed = (await preview.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, parsed["preview"]!["timeLayoutCount"]!.GetValue<int>());
        Assert.Empty(parsed["preview"]!["errors"]!.AsArray());
        ProfileTestData.AssertNativeIdsAndAttachments(ProfileDocument.Parse(parsed["profileJson"]!.GetValue<string>()));

        var saved = await PostJsonAsync(browser, "/Profiles?handler=Save", html, input);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedJson = (await saved.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = savedJson["profiles"]![0]!["id"]!.GetValue<Guid>();
        Assert.Contains("尚未下发", savedJson["message"]!.GetValue<string>());
        var exported = await browser.GetAsync($"/Profiles?handler=Export&id={id}");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/json", exported.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(exported.Content.Headers.ContentDisposition);
        var document = ProfileDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal("页面档案", document["Name"]!.GetValue<string>());
        ProfileTestData.AssertNativeIdsAndAttachments(document);

        var data = (await browser.GetFromJsonAsync<JsonObject>("/Profiles?handler=Data"))!;
        Assert.Single(data["profiles"]!.AsArray());
    }

    [Fact]
    public async Task PreviewRejectsOversizeUploadsAndSaveRejectsInvalidReferences()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var oversize = new JsonObject { ["future"] = new string('a', ProfileDocument.MaxUtf8Bytes) }.ToJsonString();
        var tooLarge = await PostJsonAsync(browser, "/Profiles?handler=Preview", html, new { profileJson = oversize });
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("5 MB", await tooLarge.Content.ReadAsStringAsync());

        var broken = ProfileDocument.Parse(ProfileTestData.Json());
        broken["Subjects"] = new JsonObject();
        var preview = await PostJsonAsync(browser, "/Profiles?handler=Preview", html, new { profileJson = broken.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.NotEmpty((await preview.Content.ReadFromJsonAsync<JsonObject>())!["preview"]!["errors"]!.AsArray());
        var save = await PostJsonAsync(browser, "/Profiles?handler=Save", html,
            new { items = new[] { ProfileTestData.New("非法引用", json: broken.ToJsonString()) } });
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        var data = (await browser.GetFromJsonAsync<JsonObject>("/Profiles?handler=Data"))!;
        Assert.Empty(data["profiles"]!.AsArray());
    }

    [Fact]
    public async Task PageRevisionConflictReturns409AndPreservesThePreviouslySavedVersion()
    {
        await using var factory = new TestWebApplicationFactory();
        StoredProfileDto original;
        using (var scope = factory.Services.CreateScope())
            original = (await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(scope.ServiceProvider), [ProfileTestData.New("原版")]))[0];
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(browser, "/Profiles?handler=Save", html,
            new { items = new[] { ProfileTestData.Edit(original, "保存的新版本") } })).StatusCode);
        var stale = await PostJsonAsync(browser, "/Profiles?handler=Save", html,
            new { items = new[] { ProfileTestData.Edit(original, "过期草稿") } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("草稿", (await stale.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());
        var stored = (await browser.GetFromJsonAsync<JsonObject>("/Profiles?handler=Data"))!["profiles"]![0]!;
        Assert.Equal("保存的新版本", stored["name"]!.GetValue<string>());
        Assert.Equal(original.Revision + 1, stored["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task ClassAdministratorCannotReadOrWriteTemplatesOrAnotherClassProfile()
    {
        await using var factory = new TestWebApplicationFactory();
        Guid firstClass;
        StoredProfileDto template;
        StoredProfileDto own;
        StoredProfileDto other;
        using (var scope = factory.Services.CreateScope())
        {
            var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
            firstClass = (await classrooms.CreateAsync("档案权限一班")).Id;
            var secondClass = (await classrooms.CreateAsync("档案权限二班")).Id;
            var actor = await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.page.classadmin", firstClass,
                AccountRole.ClassAdministratorId);
            // 另一班可访问但班内身份为学生，防止全局班主任身份被误用于授权所有班级。
            await classrooms.AddMemberAsync(secondClass, actor.Id, AccountRole.StudentId);
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            template = (await library.SaveAsync(admin, [ProfileTestData.New("保密模板")]))[0];
            var profiles = await library.SaveAsync(admin,
                [ProfileTestData.New("本班档案", firstClass), ProfileTestData.New("其他班档案", secondClass)]);
            own = profiles[0];
            other = profiles[1];
        }
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser, "profile.page.classadmin", ProfileTestData.Password);
        await SelectClassAsync(browser, firstClass);
        var html = await browser.GetStringAsync("/ClassProfiles");
        var data = ReadBootstrap(html);
        Assert.Single(data["profiles"]!.AsArray());
        Assert.Equal(own.Id, data["profiles"]![0]!["id"]!.GetValue<Guid>());
        Assert.Single(data["classes"]!.AsArray());
        Assert.DoesNotContain("href=\"/Profiles\"", WebUtility.HtmlDecode(html));
        Assert.Contains("href=\"/ClassProfiles\"", WebUtility.HtmlDecode(html));
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/Profiles?handler=Data")).StatusCode);
        foreach (var inaccessible in new[] { template, other })
        {
            var exported = await browser.GetAsync($"/ClassProfiles?handler=Export&id={inaccessible.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, exported.StatusCode);
            var save = await PostJsonAsync(browser, "/ClassProfiles?handler=Save", html,
                new { items = new[] { ProfileTestData.Edit(inaccessible, "越权覆盖") } });
            Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
            var apply = await PostJsonAsync(browser, "/ClassProfiles?handler=Apply", html, ProfileTestData.Dispatch(inaccessible));
            Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
            var delete = await PostJsonAsync(browser, "/ClassProfiles?handler=Delete", html,
                new ProfileIdRequest { Id = inaccessible.Id, Revision = inaccessible.Revision });
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
        var validSave = await PostJsonAsync(browser, "/ClassProfiles?handler=Save", html,
            new { items = new[] { ProfileTestData.Edit(own, "本班正常修改") } });
        Assert.Equal(HttpStatusCode.OK, validSave.StatusCode);
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(other.Name, (await db.StoredProfiles.AsNoTracking().SingleAsync(x => x.Id == other.Id)).Name);
        Assert.Equal(template.Name, (await db.StoredProfiles.AsNoTracking().SingleAsync(x => x.Id == template.Id)).Name);
    }

    [Fact]
    public async Task ClassPageLocksForgedDispatchTargetsToTheCurrentClass()
    {
        await using var factory = new TestWebApplicationFactory();
        Guid firstClass;
        Guid otherClass;
        StoredProfileDto own;
        using (var scope = factory.Services.CreateScope())
        {
            var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
            firstClass = (await classrooms.CreateAsync("固定下发一班")).Id;
            otherClass = (await classrooms.CreateAsync("固定下发二班")).Id;
            await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.target.classadmin", firstClass,
                AccountRole.ClassAdministratorId);
            own = (await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(scope.ServiceProvider), [ProfileTestData.New("本班下发", firstClass)]))[0];
        }
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser, "profile.target.classadmin", ProfileTestData.Password);
        await SelectClassAsync(browser, firstClass);
        var html = await browser.GetStringAsync("/ClassProfiles");
        var request = ProfileTestData.Dispatch(own);
        request.ClassIds = [otherClass];
        request.GroupIds = [Guid.NewGuid()];
        request.ConnectionIds = [Guid.NewGuid()];
        var response = await PostJsonAsync(browser, "/ClassProfiles?handler=Apply", html, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<JsonObject>())!["results"]!.AsArray());
        Assert.Equal(firstClass, result!["classId"]!.GetValue<Guid>());
        Assert.False(result["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TeacherWithSchedulePermissionIsStillDeniedProfileHandlers()
    {
        await using var factory = new TestWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
            await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.page.teacher", Classroom.DefaultId,
                AccountRole.TeacherId, UserPermissions.AccessWebUi | UserPermissions.ManageSchedule);
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser, "profile.page.teacher", ProfileTestData.Password);
        var scheduleHtml = await browser.GetStringAsync("/Schedule");
        Assert.DoesNotContain("href=\"/ClassProfiles\"", WebUtility.HtmlDecode(scheduleHtml));
        foreach (var path in new[] { "/Profiles", "/ClassProfiles" })
        {
            var page = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Contains("/Denied", page.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(path + "?handler=Data")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostJsonAsync(browser, path + "?handler=Save", scheduleHtml,
                new { items = new[] { ProfileTestData.New("非法", Classroom.DefaultId) } })).StatusCode);
        }
    }

    [Fact]
    public async Task ClassMembershipCanAuthorizeOwnProfilesWithoutGlobalOverviewPermission()
    {
        await using var factory = new TestWebApplicationFactory();
        Guid classId;
        using (var scope = factory.Services.CreateScope())
        {
            classId = (await scope.ServiceProvider.GetRequiredService<ClassroomService>().CreateAsync("班内班主任授权")).Id;
            await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.membership.admin", classId,
                AccountRole.ClassAdministratorId, globalRoleId: AccountRole.StudentId);
        }
        var auth = await factory.LoginAsync("profile.membership.admin", ProfileTestData.Password);
        Assert.False(auth.User!.Permissions.HasFlag(UserPermissions.AccessWebUi));
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser, "profile.membership.admin", ProfileTestData.Password);
        await SelectClassAsync(browser, classId);
        var html = await browser.GetStringAsync("/ClassProfiles");
        Assert.Contains("data-profile-app", html);
        var saved = await PostJsonAsync(browser, "/ClassProfiles?handler=Save", html,
            new { items = new[] { ProfileTestData.New("班内身份创建", classId) } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Single((await browser.GetFromJsonAsync<JsonObject>("/ClassProfiles?handler=Data"))!["profiles"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/Profiles?handler=Data")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mobile")]
    public async Task GenericRestAndClientChannelsRejectProfileApplyEvenForAdministrators(string? clientKind)
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var command = new CommandMessage
        {
            Command = CommandKind.ApplyProfile,
            ProfileApply = new ProfileApplyRequest
            {
                ProfileJson = ProfileTestData.Json(), Mode = ProfileApplyMode.MergeCurrent,
                Sections = ProfileDistributionSection.ClassPlans,
            },
        };
        using var client = factory.CreateClient();
        var rest = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            "/api/commands", admin.AccessToken, command));
        Assert.Equal(HttpStatusCode.Forbidden, rest.StatusCode);
        var result = (await rest.Content.ReadFromJsonAsync<CommandResult>())!;
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);

        var broadcast = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            "/api/commands/broadcast", admin.AccessToken,
            new { command = CommandKind.ApplyProfile, profileApply = command.ProfileApply, classIds = new[] { Classroom.DefaultId } }));
        Assert.True(broadcast.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);

        using var watch = await ConnectAsync(factory, admin.AccessToken, clientKind);
        await SendAsync(watch, Envelope.Command(command));
        var reply = await ReceiveAsync(watch, Protocol.MessageTypeCommandResult);
        var rejected = DeserializePayload<CommandResult>(reply);
        Assert.False(rejected.Success);
        Assert.Equal(CommandResultCodes.Forbidden, rejected.Code);

        // 读取设备档案同样只能经档案管理入口发起。
        var read = new CommandMessage { Command = CommandKind.ReadProfile };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            "/api/commands", admin.AccessToken, read))).StatusCode);
        await SendAsync(watch, Envelope.Command(read));
        var readReply = DeserializePayload<CommandResult>(await ReceiveAsync(watch, Protocol.MessageTypeCommandResult));
        Assert.Equal(CommandResultCodes.Forbidden, readReply.Code);
        await CloseAsync(watch);
    }

    [Fact]
    public async Task DispatchRejectsAnOnlineOldPluginAndSendsOnlyTheSavedVersionAfterUpgrade()
    {
        await using var factory = new TestWebApplicationFactory();
        StoredProfileDto profile;
        using (var setup = factory.Services.CreateScope())
            profile = (await setup.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(setup.ServiceProvider), [ProfileTestData.New("已保存版本", Classroom.DefaultId)]))[0];
        using var plugin = await ConnectAsync(factory, await factory.GetPluginTokenAsync());
        await ReceiveAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            Capabilities = RemoteCiCapabilities.Current.Where(x => x != RemoteCiCapabilities.ProfileApply).ToList(),
        }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => peers.HasPluginFor(Classroom.DefaultId));
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var request = ProfileTestData.Dispatch(profile);
        var unsupported = await PostJsonAsync(browser, "/Profiles?handler=Apply", html, request);
        Assert.Equal(HttpStatusCode.OK, unsupported.StatusCode);
        var denied = Assert.Single((await unsupported.Content.ReadFromJsonAsync<JsonObject>())!["results"]!.AsArray());
        Assert.False(denied!["success"]!.GetValue<bool>());
        Assert.Contains("升级", denied["message"]!.GetValue<string>());

        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => peers.PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ProfileApply));
        var pending = PostJsonAsync(browser, "/Profiles?handler=Apply", html, request);
        var envelope = await ReceiveAsync(plugin, Protocol.MessageTypeCommand);
        var forwarded = DeserializePayload<CommandMessage>(envelope);
        Assert.Equal(CommandKind.ApplyProfile, forwarded.Command);
        Assert.Equal(Classroom.DefaultId, forwarded.ClassId);
        Assert.Equal(TestWebApplicationFactory.AdminUsername, forwarded.RequestedBy!.Username);
        Assert.Equal("已保存版本", ProfileDocument.Parse(forwarded.ProfileApply!.ProfileJson)["Name"]!.GetValue<string>());
        ProfileTestData.AssertNativeIdsAndAttachments(ProfileDocument.Parse(forwarded.ProfileApply.ProfileJson));
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult, ReplyToMessageId = envelope.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "档案已应用" },
        });
        var applied = await pending;
        var done = Assert.Single((await applied.Content.ReadFromJsonAsync<JsonObject>())!["results"]!.AsArray());
        Assert.True(done!["success"]!.GetValue<bool>());
        await CloseAsync(plugin);
    }

    [Fact]
    public async Task BatchDispatchRoutesDifferentProfilesByClassAndReportsPartialFailureAndOffline()
    {
        await using var factory = new TestWebApplicationFactory();
        Guid firstClass;
        Guid secondClass;
        Guid offlineClass;
        string firstToken;
        string secondToken;
        IReadOnlyList<StoredProfileDto> profiles;
        using (var setup = factory.Services.CreateScope())
        {
            var classrooms = setup.ServiceProvider.GetRequiredService<ClassroomService>();
            firstClass = (await classrooms.CreateAsync("不同档案一班")).Id;
            secondClass = (await classrooms.CreateAsync("不同档案二班")).Id;
            offlineClass = (await classrooms.CreateAsync("离线三班")).Id;
            profiles = await setup.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(setup.ServiceProvider),
                [ProfileTestData.New("一班独立内容", firstClass), ProfileTestData.New("二班独立内容", secondClass)]);
            var identities = setup.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            firstToken = (await identities.PairPluginAsync(new PairRequest
            {
                PairCode = await identities.CreatePluginPairingCodeAsync(firstClass), Role = "plugin",
            })).Token;
            secondToken = (await identities.PairPluginAsync(new PairRequest
            {
                PairCode = await identities.CreatePluginPairingCodeAsync(secondClass), Role = "plugin",
            })).Token;
        }
        using var firstPlugin = await ConnectAsync(factory, firstToken);
        await ReceiveAsync(firstPlugin, Protocol.MessageTypeSchedulePull);
        using var secondPlugin = await ConnectAsync(factory, secondToken);
        await ReceiveAsync(secondPlugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(firstPlugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await SendAsync(secondPlugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => peers.PrimaryPluginSupports(firstClass, RemoteCiCapabilities.ProfileApply) &&
            peers.PrimaryPluginSupports(secondClass, RemoteCiCapabilities.ProfileApply));
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var request = ProfileTestData.Dispatch(profiles[0]);
        request.Items.Add(new ProfileIdRequest { Id = profiles[1].Id, Revision = profiles[1].Revision });
        request.ClassIds = [firstClass, secondClass, offlineClass];
        var responseTask = PostJsonAsync(browser, "/Profiles?handler=Apply", html, request);

        // 设备投递顺序由目标目录决定，两台设备同时接收，避免测试假定 GUID 或班级顺序。
        var firstReply = AnswerProfileAsync(firstPlugin, firstClass, "一班独立内容", success: false);
        var secondReply = AnswerProfileAsync(secondPlugin, secondClass, "二班独立内容", success: true);
        await Task.WhenAll(firstReply, secondReply);
        var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonObject>())!["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.False(results.Single(x => x!["classId"]!.GetValue<Guid>() == firstClass)!["success"]!.GetValue<bool>());
        Assert.True(results.Single(x => x!["classId"]!.GetValue<Guid>() == secondClass)!["success"]!.GetValue<bool>());
        var offline = results.Single(x => x!["classId"]!.GetValue<Guid>() == offlineClass)!;
        Assert.False(offline["success"]!.GetValue<bool>());
        Assert.Contains("未在线", offline["message"]!.GetValue<string>());
        await CloseAsync(firstPlugin);
        await CloseAsync(secondPlugin);
    }

    [Fact]
    public async Task CollectReadsTheOnlinePluginAndReturnsAnUnsavedNormalizedDraft()
    {
        await using var factory = new TestWebApplicationFactory();
        using var plugin = await ConnectAsync(factory, await factory.GetPluginTokenAsync());
        await ReceiveAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            Capabilities = RemoteCiCapabilities.Current.Where(x => x != RemoteCiCapabilities.ProfileRead).ToList(),
        }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => peers.HasPluginFor(Classroom.DefaultId));
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var request = new { classIds = new[] { Classroom.DefaultId } };

        var old = (await (await PostJsonAsync(browser, "/Profiles?handler=Collect", html, request)).Content.ReadFromJsonAsync<JsonObject>())!;
        var denied = Assert.Single(old["results"]!.AsArray())!;
        Assert.False(denied["success"]!.GetValue<bool>());
        Assert.Contains("升级", denied["message"]!.GetValue<string>());

        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => peers.PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ProfileRead));
        var pending = PostJsonAsync(browser, "/Profiles?handler=Collect", html, request);
        var envelope = await ReceiveAsync(plugin, Protocol.MessageTypeCommand);
        var forwarded = DeserializePayload<CommandMessage>(envelope);
        Assert.Equal(CommandKind.ReadProfile, forwarded.Command);
        Assert.Equal(Classroom.DefaultId, forwarded.ClassId);
        Assert.Equal(TestWebApplicationFactory.AdminUsername, forwarded.RequestedBy!.Username);
        // 设备课表的课程数多于上课时段：收集时按宿主规则截断，结果可以直接保存。
        var device = ProfileDocument.Parse(ProfileTestData.Json("设备档案"));
        device["ClassPlans"]![ProfileTestData.PlanId]!["Classes"]!.AsArray().Add(new JsonObject { ["SubjectId"] = ProfileTestData.SubjectId });
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult, ReplyToMessageId = envelope.MessageId,
            Payload = new CommandResult
            {
                Success = true, Code = CommandResultCodes.Ok, Message = "已读取设备档案", Data = ProfileDocument.Serialize(device),
            },
        });
        var collected = (await (await pending).Content.ReadFromJsonAsync<JsonObject>())!;
        var item = Assert.Single(collected["results"]!.AsArray())!;
        Assert.True(item["success"]!.GetValue<bool>(), item["message"]!.GetValue<string>());
        Assert.Empty(item["errors"]!.AsArray());
        var draft = ProfileDocument.Parse(item["profileJson"]!.GetValue<string>());
        Assert.Single(draft["ClassPlans"]![ProfileTestData.PlanId]!["Classes"]!.AsArray());
        ProfileTestData.AssertNativeIdsAndAttachments(draft);
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredProfiles.ToListAsync());
        await CloseAsync(plugin);
    }

    [Fact]
    public async Task TempLayerDispatchRequiresCapabilityAndSendsOnlyScheduledLayers()
    {
        await using var factory = new TestWebApplicationFactory();
        StoredProfileDto profile;
        using (var setup = factory.Services.CreateScope())
            profile = (await setup.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(
                await ProfileTestData.AdminAsync(setup.ServiceProvider),
                [ProfileTestData.New("带临时层", Classroom.DefaultId, json: ProfileTestData.WithTempLayer(ProfileTestData.Json(), "2099-01-05"))]))[0];
        using var plugin = await ConnectAsync(factory, await factory.GetPluginTokenAsync());
        await ReceiveAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            Capabilities = RemoteCiCapabilities.Current.Where(x => x != RemoteCiCapabilities.ProfileTempLayer).ToList(),
        }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => peers.PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ProfileApply));
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Profiles");
        var request = new ProfileDispatchRequest
        {
            Items = [new ProfileIdRequest { Id = profile.Id, Revision = profile.Revision }],
            ClassIds = [Classroom.DefaultId], Mode = ProfileApplyMode.TempLayers, ReplaceExistingTempLayers = true,
        };

        var old = (await (await PostJsonAsync(browser, "/Profiles?handler=Apply", html, request)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains("临时层", Assert.Single(old["results"]!.AsArray())!["message"]!.GetValue<string>());

        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = RemoteCiCapabilities.Current }));
        await WaitUntilAsync(() => peers.PrimaryPluginSupports(Classroom.DefaultId, RemoteCiCapabilities.ProfileTempLayer));
        var pending = PostJsonAsync(browser, "/Profiles?handler=Apply", html, request);
        var envelope = await ReceiveAsync(plugin, Protocol.MessageTypeCommand);
        var forwarded = DeserializePayload<CommandMessage>(envelope).ProfileApply!;
        Assert.Equal(ProfileApplyMode.TempLayers, forwarded.Mode);
        Assert.True(forwarded.ReplaceExistingTempLayers);
        var payload = ProfileDocument.Parse(forwarded.ProfileJson);
        Assert.StartsWith("2099-01-05", Assert.Single(payload["OrderedSchedules"]!.AsObject()).Key);
        Assert.True(payload["ClassPlans"]![ProfileTestData.OverlayPlanId]!["IsOverlay"]!.GetValue<bool>());
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult, ReplyToMessageId = envelope.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已下发 1 个临时层" },
        });
        var done = Assert.Single((await (await pending).Content.ReadFromJsonAsync<JsonObject>())!["results"]!.AsArray())!;
        Assert.True(done["success"]!.GetValue<bool>());
        Assert.Equal("已下发 1 个临时层", done["message"]!.GetValue<string>());
        await CloseAsync(plugin);
    }

    private static async Task AnswerProfileAsync(WebSocket plugin, Guid classId, string expectedName, bool success)
    {
        var envelope = await ReceiveAsync(plugin, Protocol.MessageTypeCommand);
        var command = DeserializePayload<CommandMessage>(envelope);
        Assert.Equal(CommandKind.ApplyProfile, command.Command);
        Assert.Equal(classId, command.ClassId);
        Assert.Equal(expectedName, ProfileDocument.Parse(command.ProfileApply!.ProfileJson)["Name"]!.GetValue<string>());
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult, ReplyToMessageId = envelope.MessageId,
            Payload = new CommandResult
            {
                Success = success, Code = success ? CommandResultCodes.Ok : "SAVE_FAILED",
                Message = success ? "档案已应用" : "设备保存失败",
            },
        });
    }

    private static HttpClient CreateBrowser(TestWebApplicationFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, HandleCookies = true,
    });

    private static async Task LoginWebUiAsync(HttpClient browser, string username = TestWebApplicationFactory.AdminUsername,
        string password = TestWebApplicationFactory.AdminPassword)
    {
        var html = await browser.GetStringAsync("/Login");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username, ["Input.Password"] = password, ["__RequestVerificationToken"] = Csrf(html),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task SelectClassAsync(HttpClient browser, Guid classId)
    {
        var html = await browser.GetStringAsync("/Schedule");
        var response = await browser.PostAsync("/Schedule?handler=SwitchClass", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["classId"] = classId.ToString(), ["returnUrl"] = "/ClassProfiles", ["__RequestVerificationToken"] = Csrf(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string Csrf(string html)
    {
        var match = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "页面必须提供 JSON POST 使用的防伪令牌。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient browser, string path, string html, object input)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
        request.Headers.Add("X-CSRF-TOKEN", Csrf(html));
        return browser.SendAsync(request);
    }

    private static JsonObject ReadBootstrap(string html)
    {
        var match = Regex.Match(html, "<script[^>]+data-profile-initial[^>]*>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        return JsonNode.Parse(match.Groups[1].Value)!.AsObject();
    }

    private static Task<WebSocket> ConnectAsync(TestWebApplicationFactory factory, string token, string? clientKind = null)
    {
        var kind = clientKind is null ? string.Empty : $"&client={Uri.EscapeDataString(clientKind)}";
        return factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}{kind}"), CancellationToken.None);
    }

    private static Task SendAsync(WebSocket socket, Envelope envelope) => socket.SendAsync(
        new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options)),
        WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<Envelope> ReceiveAsync(WebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, received.MessageType);
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(message.ToArray(), JsonDefaults.Options)!;
            if (envelope.Type == type) return envelope;
        }
    }

    private static T DeserializePayload<T>(Envelope envelope) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(envelope.Payload, JsonDefaults.Options), JsonDefaults.Options)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (socket.State == WebSocketState.Open)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
    }
}

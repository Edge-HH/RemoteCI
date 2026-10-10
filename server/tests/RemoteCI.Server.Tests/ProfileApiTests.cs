using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>档案 REST 接口：与 WebUI 档案页共用服务层，权限、修订号与下发校验保持一致。</summary>
public sealed class ProfileApiTests
{
    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null) =>
        TestWebApplicationFactory.Bearer(method, path, token, body);

    [Fact]
    public async Task SaveListGetCopyAndDeleteRoundTrip()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = (await factory.LoginAsync()).AccessToken;

        var saved = await client.SendAsync(Bearer(HttpMethod.Put, "/api/profiles", admin,
            new { items = new[] { new { name = "API 模板", profileJson = ProfileTestData.Json() } } }));
        saved.EnsureSuccessStatusCode();
        var template = (await saved.Content.ReadFromJsonAsync<JsonElement>())[0];
        var id = template.GetProperty("id").GetGuid();

        var list = await client.SendAsync(Bearer(HttpMethod.Get, "/api/profiles", admin));
        list.EnsureSuccessStatusCode();
        var summary = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.False(summary.TryGetProperty("profileJson", out _));
        Assert.Equal(1, summary.GetProperty("classPlanCount").GetInt32());

        var full = await client.SendAsync(Bearer(HttpMethod.Get, $"/api/profiles/{id}", admin));
        Assert.Contains("keep-root", (await full.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profileJson").GetString());

        var copy = await client.SendAsync(Bearer(HttpMethod.Post, $"/api/profiles/{id}/copy", admin, new { revision = 1, name = "API 副本" }));
        copy.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/profiles/{id}?revision=9", admin))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/profiles/{id}?revision=1", admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Bearer(HttpMethod.Get, $"/api/profiles/{id}", admin))).StatusCode);
    }

    [Fact]
    public async Task InvalidProfileIsRejectedAndPreviewReportsErrors()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = (await factory.LoginAsync()).AccessToken;

        var invalid = await client.SendAsync(Bearer(HttpMethod.Put, "/api/profiles", admin,
            new { items = new[] { new { name = "坏档案", profileJson = "{\"ClassPlans\":{\"x\":{}}}" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var preview = await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/preview", admin, new { profileJson = ProfileTestData.Json() }));
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("preview").GetProperty("timeLayoutCount").GetInt32());
        Assert.Empty(body.GetProperty("preview").GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task OrdinaryUsersCannotManageProfiles()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profiles")).StatusCode);

        var admin = (await factory.LoginAsync()).AccessToken;
        (await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin, new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "profile.reader", DisplayName = "普通用户", Password = ProfileTestData.Password,
        }))).EnsureSuccessStatusCode();
        var reader = (await factory.LoginAsync("profile.reader", ProfileTestData.Password)).AccessToken;

        var list = await client.SendAsync(Bearer(HttpMethod.Get, "/api/profiles", reader));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put, "/api/profiles", reader,
            new { items = new[] { new { name = "模板", profileJson = ProfileTestData.Json() } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Get,
            $"/api/profiles?classId={TestWebApplicationFactory.DefaultClassId}", reader))).StatusCode);
    }

    [Fact]
    public async Task ApplyRequiresExplicitChoicesAndReportsOfflineDevices()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = (await factory.LoginAsync()).AccessToken;
        var saved = await client.SendAsync(Bearer(HttpMethod.Put, "/api/profiles", admin,
            new { items = new[] { new { name = "本班档案", classId = TestWebApplicationFactory.DefaultClassId, profileJson = ProfileTestData.Json() } } }));
        saved.EnsureSuccessStatusCode();
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();

        var noMode = await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/apply", admin, new
        {
            items = new[] { new { id, revision = 1 } }, sections = 7, classIds = new[] { TestWebApplicationFactory.DefaultClassId },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noMode.StatusCode);

        var applied = await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/apply", admin, new
        {
            items = new[] { new { id, revision = 1 } }, mode = (int)ProfileApplyMode.MergeCurrent, sections = 7,
            classIds = new[] { TestWebApplicationFactory.DefaultClassId },
        }));
        applied.EnsureSuccessStatusCode();
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("未在线", result.GetProperty("results")[0].GetProperty("message").GetString());

        // 通用命令接口仍然不能绕过档案库直接下发。
        var command = await client.SendAsync(Bearer(HttpMethod.Post, "/api/commands", admin,
            new CommandMessage { Command = CommandKind.ApplyProfile, ClassId = TestWebApplicationFactory.DefaultClassId }));
        Assert.Equal(HttpStatusCode.Forbidden, command.StatusCode);

        // 扩展设置同属仅服务端命令：有班级访问权时指出专用接口（400），与此前行为一致。
        var extension = await client.SendAsync(Bearer(HttpMethod.Post, "/api/commands", admin,
            new CommandMessage { Command = CommandKind.ApplyExtensionSettings, ClassId = TestWebApplicationFactory.DefaultClassId }));
        Assert.Equal(HttpStatusCode.BadRequest, extension.StatusCode);
        var read = await client.SendAsync(Bearer(HttpMethod.Post, "/api/commands", admin,
            new CommandMessage { Command = CommandKind.ReadProfile, ClassId = TestWebApplicationFactory.DefaultClassId }));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        // 临时层下发不要求类别，但档案中必须有临时层。
        var noLayers = await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/apply", admin, new
        {
            items = new[] { new { id, revision = 1 } }, mode = (int)ProfileApplyMode.TempLayers, classIds = new[] { TestWebApplicationFactory.DefaultClassId },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noLayers.StatusCode);
    }

    [Fact]
    public async Task CollectReportsOfflineClassesWithoutSavingAndRejectsOtherClasses()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = (await factory.LoginAsync()).AccessToken;

        var collected = await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/collect", admin,
            new { classIds = new[] { TestWebApplicationFactory.DefaultClassId } }));
        collected.EnsureSuccessStatusCode();
        var body = await collected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        var result = body.GetProperty("results")[0];
        Assert.Equal(TestWebApplicationFactory.DefaultClassId, result.GetProperty("classId").GetGuid());
        Assert.Contains("未在线", result.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("profileJson").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/collect", admin,
            new { classIds = Array.Empty<Guid>() }))).StatusCode);

        (await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin, new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "profile.collector", DisplayName = "普通用户", Password = ProfileTestData.Password,
        }))).EnsureSuccessStatusCode();
        var reader = (await factory.LoginAsync("profile.collector", ProfileTestData.Password)).AccessToken;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Post, "/api/profiles/collect", reader,
            new { classIds = new[] { TestWebApplicationFactory.DefaultClassId } }))).StatusCode);
    }
}

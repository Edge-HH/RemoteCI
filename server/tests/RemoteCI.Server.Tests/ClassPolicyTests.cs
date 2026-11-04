using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>班级策略：默认值、持久化与权限/能力口径。</summary>
public sealed class ClassPolicyTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ClassPolicyTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void DefaultPolicyIsUnrestricted()
    {
        var policy = new ClassPolicy();
        Assert.True(policy.IsDefault);
        Assert.True(policy.AllowExitManagement);
        Assert.True(policy.AllowChangeConnection);
        Assert.True(policy.AllowPluginSettings);
        Assert.False(policy.DisableProfileEditing);
    }

    [Fact]
    public void AnyRestrictionMarksPolicyNonDefault()
    {
        Assert.False(new ClassPolicy { DisableDebugMenu = true }.IsDefault);
        Assert.False(new ClassPolicy { AllowExitManagement = false }.IsDefault);
        Assert.False(new ClassPolicy { AllowChangeConnection = false }.IsDefault);
        Assert.False(new ClassPolicy { AllowPluginSettings = false }.IsDefault);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    public void BrokenStoredJsonFallsBackToDefault(string json)
    {
        Assert.True(ClassPolicy.FromJson(json).IsDefault);
    }

    [Fact]
    public void PolicyRoundTripsThroughStoredJson()
    {
        var policy = new ClassPolicy
        {
            DisableProfileClassPlanEditing = true,
            DisableSettingsEditing = true,
            AllowExitManagement = false,
            AllowPluginSettings = false,
        };
        var parsed = ClassPolicy.FromJson(System.Text.Json.JsonSerializer.Serialize(policy, JsonDefaults.Options));
        Assert.True(parsed.DisableProfileClassPlanEditing);
        Assert.True(parsed.DisableSettingsEditing);
        Assert.False(parsed.AllowExitManagement);
        Assert.False(parsed.AllowPluginSettings);
        Assert.False(parsed.IsDefault);
    }

    [Fact]
    public async Task SavePersistsPolicyAndClassListExposesIt()
    {
        using var scope = _factory.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<ClassPolicyService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var policy = new ClassPolicy { DisableDebugMenu = true, AllowChangeConnection = false };

        await policies.SaveAsync([TestWebApplicationFactory.DefaultClassId], policy, CancellationToken.None);

        var stored = await policies.GetAsync(TestWebApplicationFactory.DefaultClassId, CancellationToken.None);
        Assert.True(stored.DisableDebugMenu);
        Assert.False(stored.AllowChangeConnection);
        var listed = (await classrooms.ListAsync(CancellationToken.None))
            .Single(x => x.Id == TestWebApplicationFactory.DefaultClassId);
        Assert.True(listed.Policy!.DisableDebugMenu);
        Assert.False(listed.Policy.AllowChangeConnection);

        // 恢复默认后数据库里也是默认值。
        await policies.SaveAsync([TestWebApplicationFactory.DefaultClassId], new ClassPolicy(), CancellationToken.None);
        Assert.True((await policies.GetAsync(TestWebApplicationFactory.DefaultClassId, CancellationToken.None)).IsDefault);
    }

    [Fact]
    public void PolicyCommandUsesClassIslandControlPermissionAndServerOnlyEntry()
    {
        Assert.Equal(UserPermissions.ControlClassIsland,
            CommandPermissions.Required(CommandKind.SetClassPolicy));
        Assert.True(CommandPermissions.IsServerOnly(CommandKind.SetClassPolicy));
        Assert.Equal(RemoteCiCapabilities.ClassPolicy,
            RemoteCiCapabilities.Required(CommandKind.SetClassPolicy));
        Assert.Contains(RemoteCiCapabilities.ClassPolicy, RemoteCiCapabilities.Current);
        Assert.DoesNotContain(RemoteCiCapabilities.ClassPolicy, RemoteCiCapabilities.Baseline);
    }

    [Fact]
    public void PolicyEnvelopeUsesCurrentProtocolVersion()
    {
        var envelope = Envelope.ClassPolicy(new ClassPolicy { DisableProfileEditing = true });
        Assert.Equal(Protocol.MessageTypeClassPolicy, envelope.Type);
        Assert.Equal(Protocol.Version, envelope.ProtocolVersion);
    }

    [Fact]
    public async Task ClassesPage_RendersPolicyDialogAndSavesPolicy()
    {
        using var browser = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Classes"));
        Assert.Contains("班级策略", html);
        Assert.Contains("name=\"PolicyInput.DisableProfileEditing\"", html);
        Assert.Contains("name=\"PolicyInput.AllowPluginSettings\"", html);

        var batch = WebUtility.HtmlDecode(await browser.GetStringAsync("/BatchControl"));
        Assert.Contains("data-batch-settings=\"SetClassPolicy\"", batch);
        Assert.Contains("name=\"Policy.DisableProfileEditing\"", batch);

        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        var post = await browser.PostAsync("/Classes?handler=SavePolicy", new FormUrlEncodedContent(
            new List<KeyValuePair<string, string>>
            {
                new("id", TestWebApplicationFactory.DefaultClassId.ToString()),
                // 浏览器提交勾选的复选框时先发 true、再发隐藏的 false；模型绑定必须取第一个值。
                new("PolicyInput.DisableProfileEditing", "true"),
                new("PolicyInput.DisableProfileEditing", "false"),
                new("PolicyInput.AllowExitManagement", "false"),
                new("__RequestVerificationToken", token),
            }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ClassPolicyService>()
            .GetAsync(TestWebApplicationFactory.DefaultClassId, CancellationToken.None);
        Assert.True(stored.DisableProfileEditing);
        Assert.False(stored.AllowExitManagement);
        Assert.True(stored.AllowChangeConnection);

        // 恢复默认，避免影响同一数据库上的其他测试。
        await scope.ServiceProvider.GetRequiredService<ClassPolicyService>()
            .SaveAsync([TestWebApplicationFactory.DefaultClassId], new ClassPolicy(), CancellationToken.None);
    }

    [Fact]
    public async Task BatchExecute_PersistsPolicyEvenWhenDeviceOffline()
    {
        using var browser = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/BatchControl"));
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;

        var post = await browser.PostAsync("/BatchControl?handler=Execute", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Operation"] = "SetClassPolicy",
                ["SelectedClassIds"] = TestWebApplicationFactory.DefaultClassId.ToString(),
                ["Policy.DisableDebugMenu"] = "true",
                ["Policy.AllowPluginSettings"] = "false",
                ["__RequestVerificationToken"] = token,
            }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<ClassPolicyService>();
        var stored = await policies.GetAsync(TestWebApplicationFactory.DefaultClassId, CancellationToken.None);
        Assert.True(stored.DisableDebugMenu);
        Assert.False(stored.AllowPluginSettings);
        Assert.True(stored.AllowExitManagement);

        await policies.SaveAsync([TestWebApplicationFactory.DefaultClassId], new ClassPolicy(), CancellationToken.None);
    }

    [Fact]
    public async Task RestCommandApi_RejectsPolicyCommand()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/commands?classId={TestWebApplicationFactory.DefaultClassId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        request.Content = JsonContent.Create(new CommandMessage
        {
            Command = CommandKind.SetClassPolicy,
            ClassPolicy = new ClassPolicy { DisableDebugMenu = true },
        });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("班级管理或批量控制", await response.Content.ReadAsStringAsync());
    }

    private static async Task LoginAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }
}

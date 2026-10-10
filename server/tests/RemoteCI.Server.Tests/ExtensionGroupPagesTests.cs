using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>扩展分组在 WebUI 控制页、批量控制页与扩展插件设置页上的渲染。</summary>
public sealed class ExtensionGroupPagesTests
{
    [Fact]
    public async Task ExtensionGroups_RenderOnControlBatchAndSettingsPages()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
            store.SaveExtensionGroups(Classroom.DefaultId, new[]
            {
                new ExtensionGroupDefinition
                {
                    Id = "demo.reminder",
                    DisplayName = "提醒插件",
                    Description = "定时提醒与播报",
                    Settings =
                    [
                        new ExtensionParameter
                        {
                            Key = "interval", Label = "提醒间隔", Type = ExtensionParameterType.Number,
                            Min = 1, Max = 120, Description = "两次提醒之间的分钟数",
                        },
                        new ExtensionParameter
                        {
                            Key = "voice", Label = "播报音色", Type = ExtensionParameterType.Select,
                            Options = ["standard", "soft"], OptionLabels = ["标准", "柔和"],
                        },
                    ],
                    Values = new Dictionary<string, string?> { ["interval"] = "15", ["voice"] = "soft" },
                },
            });
            store.SaveExtensions(Classroom.DefaultId, new[]
            {
                new ExtensionDefinition
                {
                    Id = "demo.reminder.now",
                    DisplayName = "立即提醒",
                    Description = "在教室端显示一条提醒",
                    GroupId = "demo.reminder",
                    RequiredPermission = UserPermissions.RunExtensions,
                    Parameters = [new ExtensionParameter { Key = "text", Label = "内容", Required = true, Multiline = true }],
                },
                new ExtensionDefinition
                {
                    Id = "demo.loose",
                    DisplayName = "未分组功能",
                    RequiredPermission = UserPermissions.RunExtensions,
                },
            });
        }

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser);

        var control = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));
        Assert.Contains("data-extension-group=\"demo.reminder\"", control);
        Assert.Contains("提醒插件", control);
        Assert.Contains("其他扩展", control);
        Assert.Contains("/ExtensionSettings?groupId=demo.reminder", control);
        Assert.Contains("在教室端显示一条提醒", control);
        Assert.Matches("<textarea name=\"ExtensionInputs\\[0\\]\\.Value\"", control);

        var batch = WebUtility.HtmlDecode(await browser.GetStringAsync("/BatchControl"));
        Assert.Contains("id=\"batch-extension-plugins\"", batch);
        Assert.Contains("data-batch-open=\"RunExtension\"", batch);
        Assert.Contains("data-batch-dialog=\"batch-settings-extension-0\"", batch);
        Assert.Contains("name=\"BatchExtensionId\" value=\"demo.reminder.now\"", batch);
        Assert.Contains("统一修改各班的 2 项设置", batch);

        var list = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings"));
        Assert.Contains("提醒插件", list);
        Assert.Contains("2 项设置 · 1 个扩展功能", list);
        Assert.Contains("仅统一管理", list);

        var settings = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings?groupId=demo.reminder"));
        Assert.Contains("当前班级", settings);
        Assert.Contains("批量下发", settings);
        Assert.Contains("各班级当前值", settings);
        Assert.Contains("允许班主任自行管理", settings);
        Assert.Contains("两次提醒之间的分钟数", settings);
        Assert.Contains(">柔和</option>", settings);
        Assert.Contains("data-extension-field-apply", settings);
        Assert.Matches("name=\"SettingInputs\\[0\\]\\.Value\"[^>]*value=\"15\"", settings);
    }

    private static async Task LoginWebUiAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }
}

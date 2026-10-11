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
            store.SaveExtensionGroups(TestWebApplicationFactory.DefaultClassId, new[]
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
            store.SaveExtensions(TestWebApplicationFactory.DefaultClassId, new[]
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
        // 控制页属于班级区，插件设置链接到只作用于当前班级的本班插件页。
        Assert.Contains("/ClassExtensions?groupId=demo.reminder", control);
        Assert.Contains("在教室端显示一条提醒", control);
        Assert.Matches("<textarea name=\"ExtensionInputs\\[0\\]\\.Value\"", control);

        var batch = WebUtility.HtmlDecode(await browser.GetStringAsync("/BatchControl"));
        Assert.Contains("id=\"batch-extension-plugins\"", batch);
        Assert.Contains("data-batch-open=\"RunExtension\"", batch);
        Assert.Contains("data-batch-dialog=\"batch-settings-extension-0\"", batch);
        Assert.Contains("name=\"BatchExtensionId\" value=\"demo.reminder.now\"", batch);
        Assert.Contains("按范围统一修改 2 项设置", batch);
        Assert.Contains("/ExtensionSettings?groupId=demo.reminder", batch);

        var list = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings"));
        Assert.Contains("提醒插件", list);
        Assert.Contains("2 项设置 · 1 个扩展功能", list);
        Assert.Contains("仅统一管理", list);

        var settings = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings?groupId=demo.reminder"));
        // 管理区按所选范围下发，不再提供“保存到当前班级”。
        Assert.DoesNotContain("保存到当前班级", settings);
        Assert.Contains("按范围下发", settings);
        Assert.Contains("data-scope-tab=\"devices\"", settings);
        Assert.Contains("name=\"SelectedClassIds\"", settings);
        Assert.Contains("各班级当前值", settings);
        Assert.Contains("允许班主任自行管理", settings);
        Assert.Contains("两次提醒之间的分钟数", settings);
        Assert.Contains(">柔和</option>", settings);
        Assert.Contains("data-extension-field-apply", settings);
        Assert.Matches("name=\"SettingInputs\\[0\\]\\.Value\"[^>]*value=\"15\"", settings);

        // 班级区的本班插件页：系统管理员也能看到，只作用于当前班级，没有范围选择。
        var classPage = WebUtility.HtmlDecode(await browser.GetStringAsync("/ClassExtensions?groupId=demo.reminder"));
        Assert.Contains("保存到本班", classPage);
        Assert.DoesNotContain("SelectedClassIds", classPage);
        Assert.DoesNotContain("SelectedConnectionIds", classPage);
        Assert.Matches("name=\"SettingInputs\\[0\\]\\.Value\"[^>]*value=\"15\"", classPage);
        Assert.Contains("href=\"/ClassExtensions\"", control);
    }

    [Fact]
    public async Task ExtensionSettings_RenderSectionsCardsConditionsAndColors()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
            store.SaveExtensionGroups(TestWebApplicationFactory.DefaultClassId, new[]
            {
                new ExtensionGroupDefinition
                {
                    Id = "demo.style",
                    DisplayName = "样式插件",
                    Settings =
                    [
                        new ExtensionParameter { Key = "enabled", Label = "运行时注入", Type = ExtensionParameterType.Switch },
                        new ExtensionParameter
                        {
                            Key = "shadow", Label = "阴影", Type = ExtensionParameterType.Switch, Section = "边框和阴影",
                            Card = "阴影", CardDescription = "添加投影效果", CardToggle = true,
                        },
                        new ExtensionParameter
                        {
                            Key = "shadowColor", Label = "阴影颜色", Type = ExtensionParameterType.Color, Section = "边框和阴影", Card = "阴影",
                            EnabledWhen = [new ExtensionFieldCondition { Key = "shadow", Values = ["true"] }],
                        },
                        new ExtensionParameter
                        {
                            Key = "shadowOpacity", Label = "阴影不透明度", Type = ExtensionParameterType.Number, Section = "边框和阴影", Card = "阴影",
                            Min = 0, Max = 1, Step = 0.05, Slider = true,
                        },
                        new ExtensionParameter
                        {
                            Key = "mode", Label = "样式", Type = ExtensionParameterType.Select, Section = "即将上课", Card = "即将上课样式",
                            Options = ["Arrows", "Scan"], OptionLabels = ["箭头", "扫描线"],
                        },
                        new ExtensionParameter
                        {
                            Key = "arrowCount", Label = "箭头组数", Type = ExtensionParameterType.Number, Section = "即将上课", Card = "箭头",
                            VisibleWhen = [new ExtensionFieldCondition { Key = "mode", Values = ["Arrows"] }],
                        },
                    ],
                    Values = new Dictionary<string, string?>
                    {
                        ["enabled"] = "true", ["shadow"] = "false", ["shadowColor"] = "#99000000",
                        ["shadowOpacity"] = "0.5", ["mode"] = "Scan", ["arrowCount"] = "3",
                    },
                },
            });
        }

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser);

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync("/ClassExtensions?groupId=demo.style"));
        Assert.Contains("data-extension-fields", page);
        Assert.Contains("<h3 class=\"extension-section-title\">边框和阴影</h3>", page);
        Assert.Contains("<strong>阴影</strong>", page);
        Assert.Contains("添加投影效果", page);
        // 卡片总开关渲染在标题行；开关值由隐藏域提交（字段名仍按声明下标）。
        Assert.Matches("data-card-toggle-slot[\\s\\S]*?name=\"SettingInputs\\[1\\]\\.Value\" value=\"false\"", page);
        // 颜色：取色器（RGB）+ 透明度（A）+ 十六进制文本（提交值）。
        Assert.Contains("type=\"color\" value=\"#000000\"", page);
        Assert.Contains("value=\"153\" data-color-alpha", page);
        Assert.Matches("name=\"SettingInputs\\[2\\]\\.Value\"[^>]*value=\"#99000000\"", page);
        // 卡片开关关闭时，依赖它的字段初始即置灰（inert）；条件交给脚本在表单变化时重算。
        Assert.Matches("data-key=\"shadowColor\"[^>]*data-enabled-when=\"[^\"]+\"[^>]*inert", page);
        // 滑块与数字框同步，步长取声明值。
        Assert.Matches("type=\"range\" min=\"0\" max=\"1\" step=\"0.05\" value=\"0.5\"", page);
        // 样式不是“箭头”，箭头卡片初始隐藏。
        Assert.Matches("data-key=\"arrowCount\"[^>]*hidden", page);

        var admin = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings?groupId=demo.style"));
        Assert.Contains("extension-values-sections", admin);
        Assert.Contains("<th colspan=\"3\">边框和阴影</th>", admin);
        Assert.Contains("<th>阴影 · 阴影颜色</th>", admin);
        Assert.Contains("extension-value-color", admin);
    }

    [Fact]
    public void ExtensionFieldValidator_ValidatesColorsAndUsesQualifiedLabels()
    {
        var fields = new List<ExtensionParameter>
        {
            new() { Key = "color", Label = "阴影颜色", Card = "阴影", Type = ExtensionParameterType.Color },
        };
        foreach (var valid in new[] { "#fff", "#8fff", "#112233", "#80112233", " #80112233 " })
        {
            var values = ExtensionFieldValidator.ValidateSettings(fields, new Dictionary<string, string?> { ["color"] = valid }, out var error);
            Assert.Null(error);
            Assert.Equal(valid.Trim(), values["color"]);
        }
        ExtensionFieldValidator.ValidateSettings(fields, new Dictionary<string, string?> { ["color"] = "red" }, out var invalid);
        Assert.Equal("“阴影 · 阴影颜色”必须是 #RRGGBB 或 #AARRGGBB 格式的颜色", invalid);
        Assert.Equal("#FF112233", ExtensionColor.Normalize("#112233"));
        Assert.Equal("#88FFFFFF", ExtensionColor.Normalize("#8fff"));

        var condition = new ExtensionFieldCondition { Key = "x", Values = ["0"], Not = true };
        Assert.False(condition.Matches("0.0"));
        Assert.True(condition.Matches("1.5"));
        Assert.True(new ExtensionFieldCondition { Key = "x", Values = ["Arrows"] }.Matches("arrows"));
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

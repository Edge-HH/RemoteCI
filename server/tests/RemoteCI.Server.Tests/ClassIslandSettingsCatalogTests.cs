using System.Text.Json;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ClassIslandSettingsCatalogTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Fact]
    public void EveryItemReferencesKnownPageAndHasUniqueKey()
    {
        var pageIds = ClassIslandSettingsCatalog.Pages.Select(page => page.Id).ToHashSet();
        Assert.Equal(ClassIslandSettingsCatalog.Pages.Count, pageIds.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Assert.All(ClassIslandSettingsCatalog.Items, item =>
        {
            Assert.True(pageIds.Contains(item.Page), $"{item.Key} 引用了未知设置页 {item.Page}");
            Assert.True(keys.Add(item.Key), $"设置项键重复：{item.Key}");
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
            if (item.Type == ClassIslandSettingType.Enum)
                Assert.NotEmpty(item.Options ?? []);
        });
    }

    [Fact]
    public void ValidatePatch_AcceptsWellFormedPatch()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["Theme"] = Json("2"),
            ["Scale"] = Json("1.25"),
            ["IsNotificationEnabled"] = Json("true"),
            ["PrimaryColor"] = Json("\"#276b5c\""),
            ["SingleWeekStartTime"] = Json("\"2026-02-23\""),
            ["ExcludedFullscreenWindow"] = Json("[\"explorer\",\"game\"]"),
            ["TaskBarIconClickBehavior"] = Json("3"),
            ["ExactTimeServer"] = Json("\"ntp.aliyun.com\""),
        };
        Assert.Null(ClassIslandSettingsCatalog.ValidatePatch(values));
    }

    [Fact]
    public void ValidatePatch_EmptyPatchIsAllowed()
    {
        Assert.Null(ClassIslandSettingsCatalog.ValidatePatch(null));
        Assert.Null(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>()));
    }

    [Theory]
    [InlineData("\"NotAKey\"", "未知的 ClassIsland 设置项")]
    [InlineData("\"SettingsOverlays\"", "未知的 ClassIsland 设置项")]
    public void ValidatePatch_RejectsUnknownKeys(string key, string expectedError)
    {
        var values = new Dictionary<string, JsonElement> { [Json(key).GetString()!] = Json("1") };
        var error = ClassIslandSettingsCatalog.ValidatePatch(values);
        Assert.NotNull(error);
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void ValidatePatch_RejectsMismatchedValues()
    {
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["IsNotificationEnabled"] = Json("\"yes\""),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["Theme"] = Json("\"dark\""),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["Scale"] = Json("\"big\""),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["Scale"] = Json("99"),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["Theme"] = Json("7"), // 枚举下标不存在
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["PrimaryColor"] = Json("\"red\""),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["SingleWeekStartTime"] = Json("\"2026/02/23\""),
        }));
        Assert.NotNull(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["ExcludedFullscreenWindow"] = Json("\"explorer\""), // 需要数组
        }));
        Assert.Null(ClassIslandSettingsCatalog.ValidatePatch(new Dictionary<string, JsonElement>
        {
            ["Scale"] = Json("1.5"),
            ["Theme"] = Json("0"),
        }));
    }

    [Fact]
    public void ValidatePatch_EnforcesValueCountLimit()
    {
        // 目录条目少于上限，因此只有构造超量的补丁才能触发数量上限；上限优先于逐项校验。
        var values = Enumerable.Range(0, ClassIslandSettingsCatalog.MaxPatchValues + 1)
            .ToDictionary(index => $"FakeSetting{index}", _ => Json("1"));
        var error = ClassIslandSettingsCatalog.ValidatePatch(values);
        Assert.NotNull(error);
        Assert.Contains("一次最多修改", error);
    }

    [Fact]
    public void IntegerDescriptors_ExposeRangeAndUnitForWebForm()
    {
        var scale = ClassIslandSettingsCatalog.Find("Scale");
        Assert.NotNull(scale);
        Assert.Equal(0.75, scale.Min);
        Assert.Equal(2.5, scale.Max);
        var theme = ClassIslandSettingsCatalog.Find("Theme");
        Assert.Equal(ClassIslandSettingType.Enum, theme!.Type);
        Assert.Equal(3, theme.Options!.Count);
    }
}

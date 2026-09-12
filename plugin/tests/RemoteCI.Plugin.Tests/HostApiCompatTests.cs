using ClassIsland.Shared.Models.Profile;
using Avalonia.Controls;
using Xunit;

namespace RemoteCI.Plugin.Tests;

/// <summary>
/// 兼容层回归测试：ClassIsland 2.2 预览版把若干宿主成员的类型改名，
/// 这些测试固定住“按成员名反射访问”的行为，避免改动时重新引入编译期绑定。
/// </summary>
public sealed class HostApiCompatTests
{
    private class BaseIcon
    {
        public string Glyph { get; set; } = string.Empty;
    }

    private sealed class DerivedIcon : BaseIcon
    {
    }

    private enum OldDecorations
    {
        Full,
        None,
    }

    private enum NewDecorations
    {
        None,
        BorderOnly,
        Full,
    }

    private sealed class OldHostWindow
    {
        public OldDecorations SystemDecorations { get; set; } = OldDecorations.Full;
    }

    private sealed class NewHostWindow
    {
        public NewDecorations WindowDecorations { get; set; } = NewDecorations.Full;
    }

    [Fact]
    public void ReadPropertyConvertsDifferentDictionaryImplementationToInterface()
    {
        // 模拟 ClassIsland 2.2：属性返回的具体字典类型变化，但都实现 IReadOnlyDictionary。
        var id = Guid.NewGuid();
        var profile = new Profile();
        profile.Subjects[id] = new Subject { Name = "语文" };

        var subjects = HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profile, "Subjects");

        Assert.Equal("语文", subjects[id].Name);
    }

    [Fact]
    public void ReadPropertyThrowsWhenMemberIsMissing()
    {
        var profile = new Profile();

        Assert.Throws<MissingMemberException>(
            () => HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profile, "NotExistingSubjects"));
    }

    [Fact]
    public void WritePropertyFindsPropertyDeclaredOnBaseType()
    {
        // 模拟 FluentAvalonia：图标字形属性声明在新宿主改名的基类上。
        var icon = new DerivedIcon();

        HostApiCompat.WriteProperty(icon, "Glyph", "\uEDB8");

        Assert.Equal("\uEDB8", icon.Glyph);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetEnumPropertySupportsRenamedEnumTypes(bool newHost)
    {
        // 模拟 Avalonia 12：装饰枚举改名，属性名保持不变。
        object window = newHost ? new NewHostWindow() : new OldHostWindow();

        HostApiCompat.SetEnumProperty(window, "None", "WindowDecorations", "SystemDecorations");

        var value = window switch
        {
            NewHostWindow newHostWindow => newHostWindow.WindowDecorations,
            OldHostWindow oldHostWindow => (object)oldHostWindow.SystemDecorations,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal("None", value.ToString());
    }

    [Fact]
    public void ReadPropertyRejectsIncompatibleValue()
    {
        var profile = new Profile();

        Assert.Throws<InvalidCastException>(
            () => HostApiCompat.ReadProperty<Profile>(profile, "Subjects"));
    }

    [Fact]
    public void BindDynamicResourcePicksAvailableOverloadOnAvaloniaBaseline()
    {
        // 当前编译基线（Avalonia 11）的 Bind 第二个参数是 IBinding；Avalonia 12 换成 BindingBase，
        // 兼容层需要按运行时参数类型挑选重载，这里固定住该行为。
        var border = new Border();

        HostApiCompat.BindDynamicResource(border, Border.BackgroundProperty, "TextFillColorPrimaryBrush");
    }
}

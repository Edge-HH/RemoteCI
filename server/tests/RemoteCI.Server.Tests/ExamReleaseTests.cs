using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ExamReleaseTests
{
    [Fact]
    public void SelectWindowsInstaller_PrefersTheSetupExecutable()
    {
        var assets = new[]
        {
            File("ExamAware-1.6.1-setup.exe.blockmap"),
            File("ExamAware-1.6.1-amd64.deb"),
            File("ExamAware-1.6.1-ia32-setup.exe"),
            File("ExamAware-1.6.1-setup.exe"),
            File("builder-debug.yml"),
        };

        var selected = ExamReleaseAssets.SelectWindowsInstaller(assets);

        Assert.Equal("ExamAware-1.6.1-setup.exe", selected?.Name);
    }

    [Fact]
    public void SelectPluginPackage_UsesTheEa2xAsset()
    {
        var assets = new[]
        {
            File("examaware-plugin-remoteci-0.1.0.zip"),
            File("examaware-plugin-remoteci-0.1.0.ea2x"),
        };

        Assert.Equal("examaware-plugin-remoteci-0.1.0.ea2x", ExamReleaseAssets.SelectPluginPackage(assets)?.Name);
        Assert.Null(ExamReleaseAssets.SelectPluginPackage([File("notes.txt")]));
    }

    [Theory]
    [InlineData("ExamAware/ExamAware2", true)]
    [InlineData("Edge-HH/ExamAware-ClassHubConnctor", true)]
    [InlineData("ExamAware/ExamAware2/extra", false)]
    [InlineData("../ExamAware", false)]
    public void IsRepository_AllowsOnlyOwnerAndName(string repository, bool allowed) =>
        Assert.Equal(allowed, ExamReleaseAssets.IsRepository(repository));

    [Fact]
    public void ReleaseForTag_BuildsThePublicDownloadUrl()
    {
        var app = ExamReleaseAssets.ReleaseForTag(
            ExamReleaseService.AppRepository,
            ExamReleaseAssets.TagFromLatestLocation(new Uri("https://github.com/ExamAware/ExamAware2/releases/tag/v1.6.1")));
        var plugin = ExamReleaseAssets.ReleaseForTag(ExamReleaseService.PluginRepository, "v0.1.0");

        Assert.Equal("ExamAware-1.6.1-setup.exe", app?.Assets[0].Name);
        Assert.Equal("https://github.com/ExamAware/ExamAware2/releases/download/v1.6.1/ExamAware-1.6.1-setup.exe", app?.Assets[0].Url);
        Assert.Equal("examaware-plugin-remoteci-0.1.0.ea2x", plugin?.Assets[0].Name);
        Assert.Null(ExamReleaseAssets.ReleaseForTag(ExamReleaseService.AppRepository, "../v1"));
    }

    private static ExamReleaseFile File(string name) => new(name, "https://github.com/ExamAware/ExamAware2/releases/download/v1.6.1/" + name, 10);
}

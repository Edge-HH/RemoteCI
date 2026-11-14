using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ExamPackageTests
{
    [Fact]
    public void Extract_PlacesThePackageBesideOtherPlugins()
    {
        var root = TempDir();
        var zip = Path.Combine(root, "plugin.ea2x");
        var plugins = Path.Combine(root, "plugins");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "package.json", """{"name":"@dsz-examaware/examaware-plugin-remoteci","examaware":{"apiVersion":2}}""");
            Write(archive, "dist/main/index.cjs", "module.exports = {}");
        }

        var installed = ExamPluginPackage.Extract(zip, plugins);

        Assert.Equal(Path.Combine(plugins, "examaware-plugin-remoteci"), installed);
        Assert.True(File.Exists(Path.Combine(installed, "dist", "main", "index.cjs")));
    }

    [Fact]
    public void Extract_RejectsEntriesThatLeaveThePluginDirectory()
    {
        var root = TempDir();
        var zip = Path.Combine(root, "plugin.ea2x");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "package.json", """{"name":"examaware-plugin-remoteci","examaware":{}}""");
            Write(archive, "../outside.txt", "nope");
        }

        var error = Assert.Throws<InvalidDataException>(() => ExamPluginPackage.Extract(zip, Path.Combine(root, "plugins")));
        Assert.Contains("路径无效", error.Message);
        Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
    }

    [Fact]
    public void Publish_WritesServerAndPairCode_AndDropsTheCodeAfterItIsCleared()
    {
        var root = TempDir();
        var path = Path.Combine(root, "connection.json");
        var settings = new PluginSettings
        {
            CloudServerUrl = "http://nas:8080",
            PluginPairCode = "class-pair-code",
        };
        var publisher = new ExamConnectionPublisher(settings, path);
        publisher.Publish();

        var first = File.ReadAllText(path);
        Assert.Contains("http://nas:8080", first);
        Assert.Contains("class-pair-code", first);

        settings.PluginPairCode = string.Empty;
        publisher.Publish();
        var second = File.ReadAllText(path);
        Assert.Contains("http://nas:8080", second);
        Assert.DoesNotContain("class-pair-code", second);
        _ = JsonSerializer.Deserialize<JsonElement>(second, JsonDefaults.Options);
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", "exam", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

using System.IO.Compression;
using System.Text;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class PluginPackageInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remoteci-package-tests-" + Guid.NewGuid().ToString("N"));

    public PluginPackageInstallServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static PluginPackageRef Package(string id, string version, bool dependency = false) => new()
    {
        PluginId = id,
        Name = id,
        Version = version,
        IsDependency = dependency,
        TransferId = Guid.NewGuid().ToString("N"),
        DownloadKey = "download-key-0123456789",
        Size = 10,
        Sha256 = new string('a', 64),
    };

    private static readonly Dictionary<string, string> Installed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["demo.a"] = "1.0.0.0",
        ["demo.dep"] = "2.0.0.0",
    };

    [Fact]
    public void Plan_InstallSkipsInstalledDependenciesButOverwritesExplicitPackages()
    {
        var plan = PluginPackageInstallService.Plan(new PluginPackageInstallRequest
        {
            Mode = PluginPackageInstallMode.Install,
            Packages = [Package("demo.a", "1.0.0.0"), Package("demo.dep", "2.1.0.0", dependency: true), Package("demo.new", "1.0.0.0", dependency: true)],
        }, Installed);

        Assert.Equal(["demo.a", "demo.new"], plan.Apply.Select(x => x.PluginId));
        Assert.Equal("demo.dep", Assert.Single(plan.Skipped).Package.PluginId);
    }

    [Fact]
    public void Plan_UpgradeKeepsOnlyInstalledAndNewerUnlessForced()
    {
        var request = new PluginPackageInstallRequest
        {
            Mode = PluginPackageInstallMode.Upgrade,
            Packages = [Package("demo.a", "1.1.0.0"), Package("demo.dep", "2.0.0.0"), Package("demo.missing", "1.0.0.0")],
        };

        Assert.Equal(["demo.a"], PluginPackageInstallService.Plan(request, Installed).Apply.Select(x => x.PluginId));

        request.Force = true;
        Assert.Equal(["demo.a", "demo.dep"], PluginPackageInstallService.Plan(request, Installed).Apply.Select(x => x.PluginId));
    }

    [Theory]
    [InlineData("..\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Validate_RejectsUnsafePluginIds(string id)
    {
        var request = new PluginPackageInstallRequest { Packages = [Package("ok", "1.0") ] };
        request.Packages[0].PluginId = id;
        Assert.NotNull(PluginPackageInstallService.Validate(request));
    }

    [Fact]
    public void Validate_RejectsDuplicatesAndAcceptsValidRequest()
    {
        Assert.Null(PluginPackageInstallService.Validate(new PluginPackageInstallRequest { Packages = [Package("demo.a", "1.0")] }));
        Assert.NotNull(PluginPackageInstallService.Validate(new PluginPackageInstallRequest
        {
            Packages = [Package("demo.a", "1.0"), Package("DEMO.A", "1.1")],
        }));
    }

    [Fact]
    public void Manifest_ReadsTopLevelIdOnly()
    {
        var path = Path.Combine(_root, "demo.cipx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.yml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("name: Demo\r\ndependencies:\r\n- id: other.plugin\r\n  isRequired: true\r\nid: 'demo.a'\r\nversion: 1.0.0.0\r\n");
        }

        var manifest = PluginPackageManifest.TryRead(path);
        Assert.NotNull(manifest);
        Assert.Equal(("demo.a", "Demo", "1.0.0.0"), (manifest.Id, manifest.Name, manifest.Version));
    }

    [Fact]
    public void Manifest_ReturnsNullForNonZip()
    {
        var path = Path.Combine(_root, "bad.cipx");
        File.WriteAllText(path, "not a zip");
        Assert.Null(PluginPackageManifest.TryRead(path));
    }
}

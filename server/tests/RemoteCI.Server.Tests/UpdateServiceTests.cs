using System.Diagnostics;
using System.Security.Cryptography;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void SelectReleaseForChannel_StableSkipsPrereleases()
    {
        var stable = new ReleaseInfo("3.1.1.0", "stable", "", [], Prerelease: false);
        var beta = new ReleaseInfo("v3.2.0-beta.1", "beta", "", [], Prerelease: true);

        var selected = UpdateService.SelectReleaseForChannel([beta, stable], UpdateChannel.Stable);

        Assert.Same(stable, selected);
    }

    [Fact]
    public void SelectReleaseForChannel_BetaIncludesPrereleases()
    {
        var draft = new ReleaseInfo("v3.3.0-beta.1", "draft", "", [], Prerelease: true, Draft: true);
        var beta = new ReleaseInfo("v3.2.0-beta.1", "beta", "", [], Prerelease: true);
        var stable = new ReleaseInfo("3.1.1.0", "stable", "", []);

        var selected = UpdateService.SelectReleaseForChannel([draft, stable, beta], UpdateChannel.Beta);

        Assert.Same(beta, selected);
    }

    [Fact]
    public void SelectReleaseForChannel_BetaPrefersStableAtSameCoreVersion()
    {
        var beta = new ReleaseInfo("v3.2.1-beta.1", "beta", "", [], Prerelease: true);
        var stable = new ReleaseInfo("3.2.1.0", "stable", "", []);

        var selected = UpdateService.SelectReleaseForChannel([beta, stable], UpdateChannel.Beta);

        Assert.Same(stable, selected);
    }

    [Fact]
    public void SelectReleaseForChannel_ExcludesOtherProtocolMajors()
    {
        var v3 = new ReleaseInfo("3.9.0.0", "v3", "", []);
        var v4 = new ReleaseInfo("4.0.0.0", "v4", "", []);

        var selected = UpdateService.SelectReleaseForChannel([v4, v3], UpdateChannel.Stable);

        Assert.Same(v3, selected);
    }

    [Fact]
    public void SelectReleaseForChannel_ExcludesLegacyVStableRelease()
    {
        var suite = new ReleaseInfo("3.1.1.0", "suite", "", []);
        var legacy = new ReleaseInfo("v3.2.0", "legacy", "", []);

        var selected = UpdateService.SelectReleaseForChannel(
            [legacy, suite],
            UpdateChannel.Stable);

        Assert.Same(suite, selected);
    }

    [Fact]
    public void ReleaseTagPredicatesAcceptCanonicalStableAndBetaOnly()
    {
        Assert.True(UpdateService.IsCanonicalStableRelease("3.2.1.0"));
        Assert.True(UpdateService.IsBetaRelease("v3.2.2-beta.1"));
        Assert.True(UpdateService.IsCurrentProtocolRelease("3.2.1.0"));
        Assert.True(UpdateService.IsCurrentProtocolRelease("v3.2.2-beta.1"));
        Assert.False(UpdateService.IsCurrentProtocolRelease("v3.2.1"));
        Assert.False(UpdateService.IsCurrentProtocolRelease("3.2.1"));
        Assert.False(UpdateService.IsCurrentProtocolRelease("4.0.0.0"));
    }

    [Fact]
    public void CanInstall_NormalUpdateRejectsSameVersion()
    {
        Assert.False(UpdateService.CanInstall("0.3.1", "0.3.1", force: false));
    }

    [Fact]
    public void CanInstall_ForceUpdateAllowsSameVersionButNotDowngrade()
    {
        Assert.True(UpdateService.CanInstall("0.3.1", "0.3.1", force: true));
        Assert.False(UpdateService.CanInstall("0.3.0", "0.3.1", force: true));
    }

    [Theory]
    [InlineData("0.4.0-beta.2", "0.4.0-beta.1")]
    [InlineData("0.4.0", "0.4.0-beta.2")]
    public void IsNewer_UsesSemanticPrereleasePrecedence(string latest, string current)
    {
        Assert.True(UpdateService.IsNewer(latest, current));
    }

    [Theory]
    [InlineData(true, false, UpdateApplyMode.ManagedByPlatform)]
    [InlineData(false, true, UpdateApplyMode.InProcessContainer)]
    [InlineData(false, false, UpdateApplyMode.ExternalInstaller)]
    public void DetermineApplyMode_SelectsSafeStrategyForRuntime(
        bool isFnos,
        bool isContainer,
        UpdateApplyMode expected) =>
        Assert.Equal(expected, UpdateService.DetermineApplyMode(isFnos, isContainer));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CanSelfUpdate_BlocksDevelopmentAndPlatformManagedRuntimes(
        bool isDevelopment,
        bool isFnos,
        bool expected) =>
        Assert.Equal(expected, UpdateService.CanSelfUpdate(isDevelopment, isFnos));

    [Fact]
    public void ResolveInstallDirectory_UsesApplicationDirectoryInsteadOfContentRoot()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", "bin", "Debug", "net10.0");

        var resolved = UpdateService.ResolveInstallDirectory(applicationDirectory);

        Assert.Equal(Path.GetFullPath(applicationDirectory), resolved);
    }

    [Fact]
    public async Task ExternalInstaller_RetriesUntilLockedDestinationCanBeReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var sourceDll = Path.Combine(source, "e_sqlite3.dll");
        var destinationDll = Path.Combine(destination, "e_sqlite3.dll");
        await File.WriteAllTextAsync(sourceDll, "new sqlite");
        await File.WriteAllTextAsync(destinationDll, "old sqlite");

        try
        {
            using var locked = new FileStream(destinationDll, FileMode.Open, FileAccess.Read, FileShare.Read);
            var applying = UpdateInstaller.ApplyFilesAsync(source, destination, CancellationToken.None);
            await Task.Delay(250);
            if (OperatingSystem.IsWindows()) Assert.False(applying.IsCompleted);
            locked.Dispose();

            await applying;

            Assert.Equal("new sqlite", await File.ReadAllTextAsync(destinationDll));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("v1.0.0-beta.1", "1.0.0-beta.1")]
    public void NormalizeVersion_StripsTagPrefix(string tag, string expected) =>
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));

    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("3.2.1.0", "3.2.0.9", true)]
    [InlineData("3.2.1.1", "3.2.1.0", true)]
    [InlineData("3.2.1.0", "3.2.1", false)]
    public void IsNewer_ComparesSemanticVersions(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Theory]
    [InlineData("../evil")]
    [InlineData("v0.4.0/../../x")]
    [InlineData("latest")]
    [InlineData("v0.4.0:with:colons")]
    public async Task PrepareUpdateAsync_RejectsMalformedReleaseTags(string tag)
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(tag, "恶意 tag", "", []);
        var asset = new ReleaseAsset("RemoteCI.Server-0.4.0-linux-x64.zip", "https://example.invalid/x.zip", 1);

        // 校验发生在任何文件/网络操作之前，可离线断言。
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareUpdateAsync(
            release, asset,
            Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", "db", "x.db"),
            "/app",
            CancellationToken.None));
    }

    [Fact]
    public async Task VerifyFileHashAsync_AcceptsMatchingHashAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "archive.zip");
            await File.WriteAllTextAsync(file, "hello remoteci");
            var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();

            await UpdateService.VerifyFileHashAsync(expected, file); // 匹配时不抛异常。

            await File.WriteAllTextAsync(file, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateService.VerifyFileHashAsync(expected, file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectServerAsset_PicksMatchingPlatformZip()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.2.0",
            "RemoteCI 0.2.0",
            "",
            new[]
            {
                new ReleaseAsset("RemoteCI.Watch-0.2.0.apk", "https://example/watch.apk", 1),
                new ReleaseAsset("RemoteCI.Plugin-0.2.0.cipx", "https://example/plugin.cipx", 1),
                new ReleaseAsset("RemoteCI.Server-0.2.0-linux-x64.zip", "https://example/linux.zip", 1),
                new ReleaseAsset("RemoteCI.Server-0.2.0-win-x64.zip", "https://example/win.zip", 1),
            });

        var expected = OperatingSystem.IsWindows() ? "win" : "linux";
        var asset = service.SelectServerAsset(release);

        Assert.NotNull(asset);
        Assert.Contains($"-{expected}-", asset.Name);
        Assert.EndsWith(".zip", asset.Name);
    }

    [Fact]
    public void SelectServerAsset_ReturnsNullWhenPlatformMissing()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.2.0",
            "RemoteCI 0.2.0",
            "",
            new[]
            {
                new ReleaseAsset("RemoteCI.Watch-0.2.0.apk", "https://example/watch.apk", 1),
            });

        Assert.Null(service.SelectServerAsset(release));
    }

    [Fact]
    public void GetUpdatesRoot_LivesNextToDatabase()
    {
        // 使用临时目录构造数据库路径，避免 Windows/Linux 对 "C:" 前缀解析不一致。
        var dataDir = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(dataDir, "remoteci.db");

        var root = UpdateService.GetUpdatesRoot(databasePath, "/app");

        Assert.Equal(Path.Combine(dataDir, "updates"), root);
    }

    [Fact]
    public async Task ApplyFilesAsync_NeverOverwritesRuntimeConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            // 更新包自带默认配置，部署目录里是用户自定义配置。
            await File.WriteAllTextAsync(Path.Combine(source, "appsettings.json"), "{\"default\":true}");
            await File.WriteAllTextAsync(Path.Combine(source, "appsettings.Production.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(source, "web.config"), "<configuration/>");
            await File.WriteAllTextAsync(Path.Combine(source, "RemoteCI.Server.dll"), "new dll");
            await File.WriteAllTextAsync(Path.Combine(destination, "appsettings.json"), "{\"Server\":{\"DatabasePath\":\"custom.db\"}}");
            await File.WriteAllTextAsync(Path.Combine(destination, "appsettings.Production.json"), "{\"user\":true}");
            await File.WriteAllTextAsync(Path.Combine(destination, "web.config"), "<user-web-config/>");

            await UpdateInstaller.ApplyFilesAsync(source, destination, CancellationToken.None);

            Assert.Equal("new dll", await File.ReadAllTextAsync(Path.Combine(destination, "RemoteCI.Server.dll")));
            Assert.Equal(
                "{\"Server\":{\"DatabasePath\":\"custom.db\"}}",
                await File.ReadAllTextAsync(Path.Combine(destination, "appsettings.json")));
            Assert.Equal("{\"user\":true}", await File.ReadAllTextAsync(Path.Combine(destination, "appsettings.Production.json")));
            Assert.Equal("<user-web-config/>", await File.ReadAllTextAsync(Path.Combine(destination, "web.config")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFilesAsync_FailureRestoresOverwrittenFilesAndRemovesNewFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            // a.dll 是本次新建；b.dll 覆盖旧版本但被旧进程只读锁住，复制必然失败。
            await File.WriteAllTextAsync(Path.Combine(source, "a.dll"), "new-a");
            await File.WriteAllTextAsync(Path.Combine(source, "b.dll"), "new-b");
            await File.WriteAllTextAsync(Path.Combine(destination, "b.dll"), "old-b");

            using (new FileStream(
                       Path.Combine(destination, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.ApplyFilesWithRollbackAsync(
                    source, destination, CancellationToken.None, maxCopyRetries: 0));
            }

            // 回滚：新建的 a.dll 被删除，b.dll 保持旧内容。
            Assert.False(File.Exists(Path.Combine(destination, "a.dll")));
            Assert.Equal("old-b", await File.ReadAllTextAsync(Path.Combine(destination, "b.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFilesAsync_SuccessRemovesStaleRuntimeFilesAndKeepsConfigAndData()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "a.dll"), "new-a");
            await File.WriteAllTextAsync(Path.Combine(destination, "a.dll"), "old-a");
            await File.WriteAllTextAsync(Path.Combine(destination, "stale.dll"), "stale");
            await File.WriteAllTextAsync(Path.Combine(destination, "appsettings.json"), "{\"user\":true}");
            await File.WriteAllTextAsync(Path.Combine(destination, "web.config"), "<user/>");
            var dataDir = Path.Combine(destination, "data");
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(dataDir, "keep.db"), "keep");

            await UpdateInstaller.ApplyFilesAsync(source, destination, CancellationToken.None);

            Assert.Equal("new-a", await File.ReadAllTextAsync(Path.Combine(destination, "a.dll")));
            Assert.False(File.Exists(Path.Combine(destination, "stale.dll")), "陈旧平面运行时文件应被清理");
            Assert.Equal("{\"user\":true}", await File.ReadAllTextAsync(Path.Combine(destination, "appsettings.json")));
            Assert.Equal("<user/>", await File.ReadAllTextAsync(Path.Combine(destination, "web.config")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(dataDir, "keep.db"))); // 子目录内容不受清理影响
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFilesWithRollback_CleanedStaleFilesAreBackedUpAndRestored()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "a.dll"), "new-a");
            await File.WriteAllTextAsync(Path.Combine(destination, "a.dll"), "old-a");
            // 旧版本独有的依赖；清理后若健康检查失败，回滚必须恢复它，否则旧版本无法启动。
            await File.WriteAllTextAsync(Path.Combine(destination, "stale.dll"), "stale");
            // 白名单收窄后，部署目录自定义的文本文件不得被清理。
            await File.WriteAllTextAsync(Path.Combine(destination, "deploy-notes.txt"), "custom");

            var (backupRoot, journal) = await UpdateInstaller.ApplyFilesWithRollbackAsync(
                source, destination, CancellationToken.None, maxCopyRetries: 0);

            Assert.False(File.Exists(Path.Combine(destination, "stale.dll")));
            Assert.Equal("custom", await File.ReadAllTextAsync(Path.Combine(destination, "deploy-notes.txt")));

            // 模拟新版本启动健康检查失败后的回滚。
            UpdateInstaller.RestoreBackups(backupRoot, destination, journal);

            Assert.Equal("stale", await File.ReadAllTextAsync(Path.Combine(destination, "stale.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyFilesAsync_SuccessCleansStaleRuntimesPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            var sourceNative = Path.Combine(source, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(sourceNative);
            await File.WriteAllTextAsync(Path.Combine(sourceNative, "e_sqlite3.dll"), "new-native");

            var targetNative = Path.Combine(destination, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(targetNative);
            await File.WriteAllTextAsync(Path.Combine(targetNative, "e_sqlite3.dll"), "old-native");
            await File.WriteAllTextAsync(Path.Combine(targetNative, "legacy-native.dll"), "stale-native");

            await UpdateInstaller.ApplyFilesAsync(source, destination, CancellationToken.None);

            Assert.Equal("new-native", await File.ReadAllTextAsync(Path.Combine(targetNative, "e_sqlite3.dll")));
            Assert.False(File.Exists(Path.Combine(targetNative, "legacy-native.dll")), "runtimes 中源已不存在的旧原生库应被清理");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WaitForStartupMarkerAsync_SucceedsWhenMarkerAppearsAndCleansUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "started.marker");
        try
        {
            using var process = Process.GetCurrentProcess(); // 运行中的进程不会提前退出。
            var writer = Task.Run(async () =>
            {
                await Task.Delay(300);
                await File.WriteAllTextAsync(marker, "ok");
            });

            var result = await UpdateInstaller.WaitForStartupMarkerAsync(marker, process, CancellationToken.None);
            await writer;

            Assert.True(result);
            Assert.False(File.Exists(marker), "标记文件应在健康检查通过后清理");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WaitForStartupMarkerAsync_FailsWhenProcessExitsWithoutMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "absent.marker");
        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c exit")
                : new ProcessStartInfo("sh", "-c exit 0");
            using var process = Process.Start(startInfo)!;

            var result = await UpdateInstaller.WaitForStartupMarkerAsync(marker, process, CancellationToken.None);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

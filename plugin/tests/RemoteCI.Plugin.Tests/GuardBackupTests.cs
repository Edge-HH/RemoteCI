using RemoteCI.Guard;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class GuardBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", "guard-backup", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private string Install => Path.Combine(_root, "ClassIsland");
    private string GuardDirectory => Path.Combine(_root, "Guard");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private GuardConfig Config() => new()
    {
        Enabled = true,
        PreventDeletion = true,
        PackageRoot = Install,
        LaunchPath = Path.Combine(Install, "ClassIsland.exe"),
        Sentinels = [Path.Combine(Install, "ClassIsland.exe")],
        BackupRoots = [Install],
        Excludes = [Path.Combine(Install, "data", "Logs")],
        ClassIslandVersion = "2.1.1.1",
        BackupIntervalHours = 24,
    };

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Install, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void SeedInstall()
    {
        Write("ClassIsland.exe", "launcher");
        Write(Path.Combine("app-2.1.1.1-0", "ClassIsland.Desktop.exe"), "desktop");
        Write(Path.Combine("data", "Settings.json"), "{}");
        Write(Path.Combine("data", "Logs", "today.log"), "log");
    }

    [Fact]
    public void Mirror_ThenRestore_BringsBackDeletedInstall_WithoutExcludedFolders()
    {
        SeedInstall();
        var config = Config();

        var backup = GuardBackup.Mirror(config, GuardDirectory, _log.Add);
        Assert.True(backup.Success, backup.Error);
        Assert.Equal(3, backup.FileCount);
        Directory.Delete(Install, true);

        var restore = GuardBackup.Restore(config, GuardDirectory, _log.Add);

        Assert.True(restore.Success, restore.Error);
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(Install, "ClassIsland.exe")));
        Assert.Equal("desktop", File.ReadAllText(Path.Combine(Install, "app-2.1.1.1-0", "ClassIsland.Desktop.exe")));
        Assert.False(File.Exists(Path.Combine(Install, "data", "Logs", "today.log")));
    }

    [Fact]
    public void Restore_OnlyFillsMissingFiles_AndKeepsNewerConfig()
    {
        SeedInstall();
        var config = Config();
        Assert.True(GuardBackup.Mirror(config, GuardDirectory, _log.Add).Success);
        File.Delete(Path.Combine(Install, "ClassIsland.exe"));
        File.WriteAllText(Path.Combine(Install, "data", "Settings.json"), "{\"changed\":true}");

        var restore = GuardBackup.Restore(config, GuardDirectory, _log.Add);

        Assert.True(restore.Success, restore.Error);
        Assert.Equal(1, restore.CopiedFiles);
        Assert.Equal("{\"changed\":true}", File.ReadAllText(Path.Combine(Install, "data", "Settings.json")));
    }

    [Fact]
    public void Mirror_IsIncremental_AndDropsFilesRemovedFromInstall()
    {
        SeedInstall();
        var config = Config();
        Assert.True(GuardBackup.Mirror(config, GuardDirectory, _log.Add).Success);
        File.Delete(Path.Combine(Install, "data", "Settings.json"));
        Write(Path.Combine("data", "Profiles", "Default.json"), "profile");

        var second = GuardBackup.Mirror(config, GuardDirectory, _log.Add);

        Assert.True(second.Success, second.Error);
        Assert.Equal(1, second.CopiedFiles);
        var current = Path.Combine(GuardBackup.CurrentDirectory(GuardDirectory), "0");
        Assert.False(File.Exists(Path.Combine(current, "data", "Settings.json")));
        Assert.True(File.Exists(Path.Combine(current, "data", "Profiles", "Default.json")));
    }

    [Fact]
    public void Mirror_RefusesToSyncMassDeletion()
    {
        for (var i = 0; i < GuardBackup.MassDeletionMinFiles + 10; i++) Write(Path.Combine("app", $"lib{i}.dll"), "x");
        Write("ClassIsland.exe", "launcher");
        var config = Config();
        Assert.True(GuardBackup.Mirror(config, GuardDirectory, _log.Add).Success);
        Directory.Delete(Path.Combine(Install, "app"), true);

        var result = GuardBackup.Mirror(config, GuardDirectory, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("疑似正在被删除", result.Error);
        Assert.Equal(GuardBackup.MassDeletionMinFiles + 11,
            Directory.GetFiles(GuardBackup.CurrentDirectory(GuardDirectory), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Restore_FailsWithoutBackup()
    {
        var result = GuardBackup.Restore(Config(), GuardDirectory, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("没有可用的备份", result.Error);
    }

    [Fact]
    public void IsDue_ForFirstBackup_VersionChange_IntervalAndRetryBackoff()
    {
        var config = Config();
        const long now = 10_000_000_000;
        var manifest = new GuardBackupManifest
        {
            Roots = [Path.GetFullPath(Install)],
            ClassIslandVersion = "2.1.1.1",
            CreatedAtUnixMs = now - 60 * 60_000,
        };

        Assert.True(GuardBackup.IsDue(config, null, 0, now));
        Assert.False(GuardBackup.IsDue(config, manifest, 0, now));
        Assert.True(GuardBackup.IsDue(new GuardConfig { ClassIslandVersion = "2.2.0.0", BackupRoots = config.BackupRoots }, manifest, 0, now));
        Assert.True(GuardBackup.IsDue(config, manifest, 0, now + 24 * 60 * 60_000L));
        Assert.False(GuardBackup.IsDue(config, null, now - 10 * 60_000, now));
    }

    [Fact]
    public void ExtraBackupTarget_SurvivesDefaultBackupLoss()
    {
        SeedInstall();
        var extraRoot = Path.Combine(_root, "external");
        var config = Config();
        config.BackupPaths = [extraRoot];

        var backup = GuardBackup.Mirror(config, GuardDirectory, _log.Add);

        Assert.True(backup.Success, backup.Error);
        var extra = Path.Combine(extraRoot, "RemoteCI.Guard.Backup");
        Assert.True(File.Exists(GuardBackup.ManifestPath(extra)));
        Directory.Delete(GuardDirectory, true);
        Directory.Delete(Install, true);
        var restored = GuardBackup.Restore(config, GuardDirectory, _log.Add);
        Assert.True(restored.Success, restored.Error);
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(Install, "ClassIsland.exe")));
    }

    [Fact]
    public void ProtectedBackup_CustomDirectorySurvivesDefaultLoss_AndRandomDirectoryIsReused()
    {
        SeedInstall();
        var root = Path.Combine(_root, "ProtectedBackups");
        var config = Config();
        config.BackupDirectoryName = "Campus.Backup";
        GuardBackup.ConfigureProtectedBackup(config, true, _log.Add, root);

        Assert.True(config.ProtectedBackupReady);
        Assert.Equal(Path.Combine(root, "Campus.Backup"), config.ProtectedBackupPath);
        if (OperatingSystem.IsWindows())
        {
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            var rules = new DirectoryInfo(config.ProtectedBackupPath).GetAccessControl()
                .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .OfType<System.Security.AccessControl.FileSystemAccessRule>();
            Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(users) &&
                rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow);
        }
        Assert.True(GuardBackup.Mirror(config, GuardDirectory, _log.Add).Success);
        Directory.Delete(GuardDirectory, true);
        Directory.Delete(Install, true);
        Assert.True(GuardBackup.Restore(config, GuardDirectory, _log.Add).Success);
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(Install, "ClassIsland.exe")));

        var custom = config.ProtectedBackupPath;
        config.BackupDirectoryName = string.Empty;
        GuardBackup.ConfigureProtectedBackup(config, true, _log.Add, root);
        var random = config.ProtectedBackupPath;
        Assert.NotEqual(custom, random);
        Assert.False(Directory.Exists(custom));
        GuardBackup.ConfigureProtectedBackup(config, true, _log.Add, root);
        Assert.Equal(random, config.ProtectedBackupPath);
        Assert.Equal(32, Path.GetFileName(random!).Length);

        GuardBackup.ConfigureProtectedBackup(config, false, _log.Add, root);
        Assert.False(config.ProtectedBackupReady);
        Assert.Null(config.ProtectedBackupPath);
    }

    [Fact]
    public void ProtectedBackup_InvalidDirectoryNameFallsBackToRandom()
    {
        var config = Config();
        config.BackupDirectoryName = "../outside";

        GuardBackup.ConfigureProtectedBackup(config, true, _log.Add, Path.Combine(_root, "ProtectedBackups"));

        Assert.Equal(string.Empty, config.BackupDirectoryName);
        Assert.Equal(32, Path.GetFileName(config.ProtectedBackupPath!).Length);
        Assert.True(GuardBackup.IsUnder(config.ProtectedBackupPath!, Path.Combine(_root, "ProtectedBackups")));
    }

    [Fact]
    public void Contracts_RoundTripThroughDataContractJson()
    {
        var path = Path.Combine(GuardDirectory, GuardFiles.Policy);
        GuardJson.WriteFile(path, Config());
        GuardJson.WriteFile(path, Config());

        var loaded = GuardJson.ReadFile<GuardConfig>(path)!;

        Assert.True(loaded.PreventDeletion);
        Assert.Equal(Config().Sentinels, loaded.Sentinels);
        Assert.Contains("\"launchPath\"", File.ReadAllText(path));
        Assert.Null(GuardJson.Deserialize<GuardConfig>("not json"));
    }
}

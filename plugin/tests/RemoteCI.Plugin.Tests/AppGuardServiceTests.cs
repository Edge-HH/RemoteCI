using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Guard;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class AppGuardServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", "app-guard", Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host;

    public AppGuardServiceTests()
    {
        _host = new FakeHost(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private string GuardDirectory => Path.Combine(_root, "Guard");

    private (AppGuardStore Store, AppGuardService Service) Create()
    {
        var store = new AppGuardStore(Path.Combine(_root, "config", "AppGuardPolicy.json"));
        return (store, new AppGuardService(store, _host, NullLogger<AppGuardService>.Instance, GuardDirectory));
    }

    private static AppGuardPolicy Enabled() => new()
    {
        Enabled = true, RestartAfterExit = true, RestartAfterExitDelayMinutes = 40, RestartOnCrash = true, PreventDeletion = true,
        OrganizationName = "实验中学",
    };

    [Fact]
    public void EnabledPolicy_DeploysGuardExecutable_ConfigRunKeyAndStartsIt()
    {
        var (store, service) = Create();
        service.Start();

        store.Apply(Enabled());

        Assert.Equal("guard-v1", File.ReadAllText(service.GuardExecutablePath));
        var config = GuardJson.ReadFile<GuardConfig>(Path.Combine(GuardDirectory, GuardFiles.Policy))!;
        Assert.True(config.PreventDeletion);
        Assert.Equal(40, config.RestartDelayMinutes);
        Assert.Equal(_host.Info.LaunchPath, config.LaunchPath);
        Assert.Equal([_host.Info.LaunchPath], config.Sentinels);
        Assert.Equal([_host.Info.PackageRoot, _host.Info.DataRoots[1]], config.BackupRoots);
        var state = GuardJson.ReadFile<GuardRunState>(Path.Combine(GuardDirectory, GuardFiles.State))!;
        Assert.Equal(GuardRunStates.Running, state.State);
        Assert.Equal(4242, state.Pid);
        Assert.Equal($"\"{service.GuardExecutablePath}\"", _host.RunKey);
        Assert.Equal(1, _host.Starts);
        service.Stop();
    }

    [Fact]
    public void NewBundledGuard_ReplacesRunningCopy()
    {
        var (store, service) = Create();
        service.Start();
        store.Apply(Enabled());
        File.WriteAllText(_host.BundledGuardPath, "guard-v2");

        var changed = Enabled();
        changed.RestartAfterExitDelayMinutes = 10;
        store.Apply(changed);

        Assert.Equal("guard-v2", File.ReadAllText(service.GuardExecutablePath));
        Assert.Equal(1, _host.Stops);
        service.Stop();
    }

    [Fact]
    public void DisabledPolicy_StopsGuardRemovesRunKeyAndDeletesBackups()
    {
        var (store, service) = Create();
        service.Start();
        store.Apply(Enabled());
        Directory.CreateDirectory(Path.Combine(GuardDirectory, "backup", "current"));

        store.Apply(new AppGuardPolicy { Enabled = false });

        Assert.False(Directory.Exists(GuardDirectory));
        Assert.Null(_host.RunKey);
        Assert.True(_host.Stops >= 1);
        service.Stop();
    }

    [Fact]
    public void RemoteClose_StopsGuardWithoutDeletingInstallation()
    {
        var (store, service) = Create();
        service.Start();
        var enabled = Enabled();
        enabled.EnhancedMode = true;
        store.Apply(enabled);
        var backup = Path.Combine(GuardDirectory, "backup", "current");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(GuardDirectory, GuardFiles.StopRequested), "1");

        var closed = Enabled();
        closed.Enabled = false;
        store.Apply(closed);

        Assert.True(Directory.Exists(GuardDirectory));
        Assert.True(Directory.Exists(backup));
        Assert.True(File.Exists(service.GuardExecutablePath));
        Assert.True(File.Exists(Path.Combine(GuardDirectory, GuardFiles.StopRequested)));
        Assert.Null(_host.RunKey);
        Assert.True(_host.Stops >= 1);
        Assert.True(_host.EnhancedCalls >= 1);

        store.Apply(enabled);
        Assert.False(File.Exists(Path.Combine(GuardDirectory, GuardFiles.StopRequested)));
        Assert.True(_host.GuardRunning);
        service.Stop();
    }

    [Fact]
    public void NoPolicyYet_LeavesMachineUntouched()
    {
        var (_, service) = Create();

        service.Start();

        Assert.False(Directory.Exists(GuardDirectory));
        Assert.Equal("untouched", _host.RunKey);
        service.Stop();
    }

    [Fact]
    public void MarkExiting_DoesNotOverwriteRecentUpdatingMarker()
    {
        var (store, service) = Create();
        service.Start();
        store.Apply(Enabled());
        var statePath = Path.Combine(GuardDirectory, GuardFiles.State);

        service.MarkExiting();
        Assert.Equal(GuardRunStates.Exiting, GuardJson.ReadFile<GuardRunState>(statePath)!.State);
        service.MarkUpdating();
        service.MarkExiting();

        Assert.Equal(GuardRunStates.Updating, GuardJson.ReadFile<GuardRunState>(statePath)!.State);
        service.Stop();
    }

    [Fact]
    public void PendingNotice_IsShownOnceWithOrganizationName()
    {
        var (store, service) = Create();
        store.Apply(Enabled());
        Directory.CreateDirectory(GuardDirectory);
        GuardJson.WriteFile(Path.Combine(GuardDirectory, GuardFiles.PendingNotice), new GuardNotice { AtUnixMs = 1 });

        service.Start();
        service.ShowPendingNotice();

        var notice = Assert.Single(_host.Notices);
        Assert.Equal(AppGuardService.NoticeHeader, notice.Header);
        Assert.Equal("当前ClassIsland由实验中学统一管理，请勿自行删除，当前实例已由RemoteCI自动恢复。", notice.Message);
        service.Stop();
    }

    [Fact]
    public async Task FlushEvents_SendsGuardEventsAndKeepsThemWhenDeliveryFails()
    {
        var (store, service) = Create();
        store.Apply(Enabled());
        Directory.CreateDirectory(GuardDirectory);
        var line = GuardJson.Serialize(new GuardEventLine { Kind = GuardEventKinds.Restored, AtUnixMs = 123, Detail = "已恢复" });
        File.WriteAllText(Path.Combine(GuardDirectory, GuardFiles.Events), line + "\n\n");
        var sent = new List<AppGuardEventBatch>();
        var deliver = false;
        service.EventSender = batch =>
        {
            sent.Add(batch);
            return Task.FromResult(deliver);
        };

        await service.FlushEventsAsync();
        Assert.True(File.Exists(Path.Combine(GuardDirectory, GuardFiles.EventsSending)));
        deliver = true;
        await service.FlushEventsAsync();

        Assert.Equal(2, sent.Count);
        var item = Assert.Single(sent[1].Events);
        Assert.Equal(AppGuardEventKinds.Restored, item.Kind);
        Assert.Equal("已恢复", item.Detail);
        Assert.False(File.Exists(Path.Combine(GuardDirectory, GuardFiles.EventsSending)));
    }

    [Fact]
    public void MsixInstall_DisablesPreventDeletionOnly()
    {
        var info = _host.Info with { IsMsix = true };

        var config = AppGuardService.BuildConfig(Enabled(), info, 1);

        Assert.False(config.PreventDeletion);
        Assert.True(config.RestartOnCrash);
    }

    [Fact]
    public void Store_PersistsPolicy_AndPolicyMessageIsReadFromEnvelope()
    {
        var path = Path.Combine(_root, "config", "AppGuardPolicy.json");
        var store = new AppGuardStore(path);
        var changed = 0;
        store.Changed += () => changed++;

        Assert.True(AppGuardPolicyMessage.TryRead(Envelope.AppGuardPolicy(Enabled()), out var policy));
        store.Apply(policy);
        store.Apply(policy);

        Assert.Equal(1, changed);
        Assert.Equal("实验中学", new AppGuardStore(path).Current!.OrganizationName);
        Assert.False(AppGuardPolicyMessage.TryRead(Envelope.HolidayCalendar(new HolidayCalendar()), out _));
    }

    [Fact]
    public void GuardManagementCommands_AreServerOnlyAdminOperations()
    {
        foreach (var command in new[] { CommandKind.StopAppGuard, CommandKind.UninstallAppGuard })
        {
            Assert.True(CommandPermissions.IsServerOnly(command));
            Assert.Equal(UserPermissions.ManageUsers, CommandPermissions.Required(command));
            Assert.Equal(RemoteCiCapabilities.AppGuardManage, RemoteCiCapabilities.Required(command));
        }
        Assert.DoesNotContain(RemoteCiCapabilities.AppGuardManage, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.AppGuardManage, RemoteCiCapabilities.Current);
    }

    [Fact]
    public void EnhancedFailure_FallsBackToStandardGuardAndConnectionTokenIsProtected()
    {
        const string token = "device-secret-token";
        _host.EnhancedResult = false;
        var (store, service) = Create();
        service.ConnectionInfoProvider = () => (true, "https://example.test", token);
        service.Start();

        var policy = Enabled();
        policy.EnhancedMode = true;
        store.Apply(policy);

        Assert.True(_host.GuardRunning);
        Assert.True(_host.EnhancedCalls >= 1);
        var events = File.ReadAllText(Path.Combine(GuardDirectory, GuardFiles.Events));
        Assert.Contains("enhanced-failed", events);
        var connection = File.ReadAllText(Path.Combine(GuardDirectory, GuardFiles.Connection));
        Assert.DoesNotContain(token, connection);
        service.Stop();
    }

    [Fact]
    public async Task CachedEnhancedPolicy_DoesNotBlockServiceStart()
    {
        var (store, service) = Create();
        var policy = Enabled();
        policy.EnhancedMode = true;
        store.Apply(policy);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.EnhancedHandler = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return true;
        };

        var start = Task.Run(service.Start);
        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "缓存策略仍应在后台部署");
        }
        finally
        {
            release.Set();
        }
        service.Stop();
    }

    [Theory]
    [InlineData("Campus.Agent.exe", "Campus.Agent")]
    [InlineData(" invalid / name ", "RemoteCI.Guard")]
    [InlineData(".hidden", "RemoteCI.Guard")]
    public void ProcessName_IsNormalized(string value, string expected)
    {
        Assert.Equal(expected, GuardFiles.NormalizeProcessName(value));
        Assert.Equal(expected, new AppGuardPolicy { ProcessName = value }.Normalized().ProcessName);
    }

    [Fact]
    public void ProcessRename_DeploysOutsideClassIsland_StopsOldGuardAndUpdatesRunKey()
    {
        var (store, service) = Create();
        service.Start();
        store.Apply(Enabled());
        var previous = service.GuardExecutablePath;

        var renamed = Enabled();
        renamed.ProcessName = "Campus.Agent";
        store.Apply(renamed);

        Assert.EndsWith("Campus.Agent.exe", service.GuardExecutablePath);
        Assert.False(GuardBackup.IsUnder(service.GuardExecutablePath, _host.Info.PackageRoot));
        Assert.False(File.Exists(previous));
        Assert.Contains(previous, _host.StoppedPaths);
        Assert.Equal($"\"{service.GuardExecutablePath}\"", _host.RunKey);
        Assert.Equal("Campus.Agent", GuardJson.ReadFile<GuardConfig>(Path.Combine(GuardDirectory, GuardFiles.Policy))!.ProcessName);
        service.Stop();
    }

    [Fact]
    public void ProcessRename_UpdatesEnhancedAgentPath()
    {
        var (store, service) = Create();
        service.Start();
        var enhanced = Enabled();
        enhanced.EnhancedMode = true;
        store.Apply(enhanced);
        var firstTarget = service.GuardExecutablePath;

        enhanced.ProcessName = "Campus.Agent";
        store.Apply(enhanced);

        Assert.Equal(2, _host.EnhancedCalls);
        Assert.Equal(service.GuardExecutablePath, _host.LastEnhancedExecutablePath);
        Assert.NotEqual(firstTarget, _host.LastEnhancedExecutablePath);
        var marker = GuardJson.ReadFile<EnhancedGuardConfig>(_host.EnhancedMarkerPath)!;
        Assert.Equal(service.GuardExecutablePath, marker.AgentExecutablePath);
        service.Stop();
    }

    private sealed class FakeHost : IAppGuardHost
    {
        public FakeHost(string root)
        {
            var package = Path.Combine(root, "ClassIsland");
            Directory.CreateDirectory(package);
            Info = new AppGuardHostInfo(
                package,
                Path.Combine(package, "ClassIsland.exe"),
                Path.Combine(package, "app-2.1.1.1-0", "ClassIsland.Desktop.exe"),
                [Path.Combine(package, "data"), Path.Combine(root, "LocalAppData", "ClassIsland")],
                [Path.Combine(package, "data", "Logs")],
                "2.1.1.1",
                false);
            BundledGuardPath = Path.Combine(root, "plugin", GuardFiles.ExecutableName);
            EnhancedMarkerPath = Path.Combine(root, "ProgramData", "enhanced.json");
            Directory.CreateDirectory(Path.GetDirectoryName(BundledGuardPath)!);
            File.WriteAllText(BundledGuardPath, "guard-v1");
        }

        public AppGuardHostInfo Info { get; }
        public string BundledGuardPath { get; }
        public string EnhancedMarkerPath { get; }
        public int CurrentProcessId => 4242;
        public string? RunKey { get; private set; } = "untouched";
        public bool GuardRunning { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int EnhancedCalls { get; private set; }
        public bool EnhancedResult { get; set; } = true;
        public Func<bool>? EnhancedHandler { get; set; }
        public string? LastEnhancedExecutablePath { get; private set; }
        public List<string> StoppedPaths { get; } = [];
        public List<(string Header, string Message)> Notices { get; } = [];

        public AppGuardHostInfo Describe() => Info;
        public bool IsGuardRunning(string executablePath) => GuardRunning;

        public void StopGuard(string executablePath)
        {
            Stops++;
            StoppedPaths.Add(executablePath);
            GuardRunning = false;
        }

        public void StartGuard(string executablePath, string directory)
        {
            Starts++;
            GuardRunning = true;
        }

        public void SetRunKey(string? command) => RunKey = command;
        public bool ConfigureEnhanced(bool enabled, string executablePath, string directory)
        {
            EnhancedCalls++;
            LastEnhancedExecutablePath = executablePath;
            if (EnhancedHandler is not null) return EnhancedHandler();
            if (EnhancedResult)
            {
                if (enabled)
                    GuardJson.WriteFile(EnhancedMarkerPath, new EnhancedGuardConfig
                    {
                        AgentDirectory = directory,
                        AgentExecutablePath = executablePath,
                    });
                else if (File.Exists(EnhancedMarkerPath)) File.Delete(EnhancedMarkerPath);
            }
            return EnhancedResult;
        }
        public void ShowNotice(string header, string message) => Notices.Add((header, message));
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace RemoteCI.Guard;

internal static class EnhancedGuardSetup
{
    public const string ServiceName = "RemoteCI.Guard";
    public const string TaskName = @"RemoteCI\Guard Agent";

    public static string ProgramDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteCI", "Guard");

    public static int Install(string agentDirectory)
    {
        var policy = GuardJson.ReadFile<GuardConfig>(Path.Combine(agentDirectory, GuardFiles.Policy));
        var agentExe = Path.Combine(agentDirectory, GuardFiles.ExecutableNameFor(policy?.ProcessName));
        if (!File.Exists(agentExe)) return 2;
        var programData = ProgramDataDirectory();
        Directory.CreateDirectory(programData);
        Run("sc.exe", $"stop {ServiceName}", 10_000, allowFailure: true);
        var serviceExe = Path.Combine(programData, "RemoteCI.Guard.Service.exe");
        File.Copy(Process.GetCurrentProcess().MainModule?.FileName ?? typeof(EnhancedGuardSetup).Assembly.Location, serviceExe, true);
        foreach (var dependency in Directory.EnumerateFiles(agentDirectory, "RemoteCI.Guard.*"))
        {
            if (!string.Equals(dependency, Process.GetCurrentProcess().MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                File.Copy(dependency, Path.Combine(programData, Path.GetFileName(dependency)), true);
        }
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var action = $"\"{agentExe}\" \"{agentDirectory}\"";
        if (Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /TR \"{action}\" /SC ONLOGON /RU \"{user}\" /IT /RL LIMITED /F") != 0) return 2;
        var serviceOperation = Run("sc.exe", $"query {ServiceName}", allowFailure: true) == 0 ? "config" : "create";
        if (Run("sc.exe", $"{serviceOperation} {ServiceName} binPath= \"\\\"{serviceExe}\\\" --service\" start= auto DisplayName= \"RemoteCI 应用守护服务\"") != 0) return 3;
        if (Run("sc.exe", $"description {ServiceName} \"维护 RemoteCI 标准权限用户代理；不以管理员身份启动 ClassIsland。\"") != 0) return 3;
        GuardJson.WriteFile(Path.Combine(programData, "enhanced.json"), new EnhancedGuardConfig
        {
            AgentDirectory = agentDirectory,
            AgentExecutablePath = agentExe,
            TaskName = TaskName,
        });
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        return Run("sc.exe", $"start {ServiceName}", 15_000) == 0 ? 0 : 4;
    }

    public static int Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}", 10_000, allowFailure: true);
        Run("sc.exe", $"delete {ServiceName}", allowFailure: true);
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", allowFailure: true);
        try { Directory.Delete(ProgramDataDirectory(), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    private static int Run(string fileName, string arguments, int timeoutMs = 30_000, bool allowFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill();
            throw new System.TimeoutException($"{fileName} 执行超时");
        }
        if (!allowFailure && process.ExitCode != 0)
            Debug.WriteLine($"{fileName} {arguments}: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
        return process.ExitCode;
    }
}

internal sealed class EnhancedGuardService : ServiceBase
{
    private Timer? _timer;

    public EnhancedGuardService()
    {
        ServiceName = EnhancedGuardSetup.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _timer = new Timer(_ => Maintain(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    protected override void OnStop() => _timer?.Dispose();

    private void Maintain()
    {
        try
        {
            var config = GuardJson.ReadFile<EnhancedGuardConfig>(
                Path.Combine(EnhancedGuardSetup.ProgramDataDirectory(), "enhanced.json"));
            if (config is null) return;
            var policy = GuardJson.ReadFile<GuardConfig>(Path.Combine(config.AgentDirectory, GuardFiles.Policy));
            if (policy is not { Enabled: true, EnhancedMode: true }) return;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{config.TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"RemoteCI enhanced guard maintenance failed: {ex.Message}");
        }
    }
}

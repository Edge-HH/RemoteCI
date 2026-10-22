using System.Diagnostics;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using Microsoft.Win32;
using RemoteCI.Guard;

namespace RemoteCI.Plugin.Services;

/// <summary>ClassIsland 的安装位置与本次运行信息，用来生成守护进程配置。</summary>
public sealed record AppGuardHostInfo(
    string PackageRoot,
    string LaunchPath,
    string ProcessPath,
    IReadOnlyList<string> DataRoots,
    IReadOnlyList<string> Excludes,
    string? ClassIslandVersion,
    bool IsMsix);

/// <summary>应用守护与宿主、进程、注册表打交道的部分；隔离出来使 <see cref="AppGuardService"/> 可单元测试。</summary>
public interface IAppGuardHost
{
    AppGuardHostInfo Describe();

    /// <summary>插件包里随附的 RemoteCI.Guard.exe。</summary>
    string BundledGuardPath { get; }

    int CurrentProcessId { get; }

    bool IsGuardRunning(string executablePath);

    void StopGuard(string executablePath);

    void StartGuard(string executablePath, string directory);

    /// <summary>写入或删除（<paramref name="command"/> 为 null）当前用户的登录自启项。</summary>
    void SetRunKey(string? command);

    /// <summary>用 ClassIsland 原生对话框提示。</summary>
    void ShowNotice(string header, string message);
}

public sealed class ClassIslandAppGuardHost : IAppGuardHost
{
    public string BundledGuardPath =>
        Path.Combine(Path.GetDirectoryName(typeof(ClassIslandAppGuardHost).Assembly.Location) ?? AppContext.BaseDirectory, GuardFiles.ExecutableName);

    public int CurrentProcessId => Environment.ProcessId;

    public AppGuardHostInfo Describe()
    {
        var processPath = Environment.ProcessPath ?? string.Empty;
        var packageRoot = FullPath(CommonDirectories.AppPackageRoot);
        if (string.IsNullOrEmpty(packageRoot) || !Directory.Exists(packageRoot))
            packageRoot = FullPath(Path.GetDirectoryName(processPath));
        // 文件夹版 ClassIsland 根目录有启动器 ClassIsland.exe，实际程序在 app-版本 子目录；优先启动启动器，升级后也能找到新版本。
        var launcher = Path.Combine(packageRoot, "ClassIsland.exe");
        var launchPath = File.Exists(launcher) ? launcher
            : File.Exists(AppBase.ExecutingEntrance) ? AppBase.ExecutingEntrance
            : processPath;
        var dataRoots = new[] { CommonDirectories.AppRootFolderPath, CommonDirectories.AppDataFolderPath }
            .Select(FullPath)
            .Where(x => x.Length > 0 && Directory.Exists(x))
            .ToList();
        var excludes = new[] { CommonDirectories.AppLogFolderPath, CommonDirectories.AppCacheFolderPath, CommonDirectories.AppTempFolderPath }
            .Select(FullPath)
            .Where(x => x.Length > 0)
            .ToList();
        return new AppGuardHostInfo(packageRoot, launchPath, processPath, dataRoots, excludes, AppBase.AppVersion, AppBase.Current.IsMsix);
    }

    public bool IsGuardRunning(string executablePath) => FindGuards(executablePath).Any();

    public void StopGuard(string executablePath)
    {
        foreach (var process in FindGuards(executablePath))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    public void StartGuard(string executablePath, string directory)
    {
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            ArgumentList = { directory },
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public void SetRunKey(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GuardFiles.RunKeyPath);
        if (command is null) key.DeleteValue(GuardFiles.RunValueName, false);
        else key.SetValue(GuardFiles.RunValueName, command);
    }

    public void ShowNotice(string header, string message) => Dispatcher.UIThread.Post(async () =>
    {
        try { await CommonTaskDialogs.ShowDialog(header, message, AppBase.Current.GetRootWindow()); }
        catch (Exception ex) { Console.Error.WriteLine($"RemoteCI 应用守护提示显示失败：{ex}"); }
    });

    private static IEnumerable<Process> FindGuards(string executablePath)
    {
        foreach (var process in Process.GetProcessesByName(GuardFiles.ProcessName))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            if (path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
                yield return process;
            else process.Dispose();
        }
    }

    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }
}

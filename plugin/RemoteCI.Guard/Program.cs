using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCI.Guard;

/// <summary>
/// RemoteCI 应用守护进程：由插件部署到 %LOCALAPPDATA%\RemoteCI\Guard 并登记为登录自启，
/// 在 ClassIsland 之外负责退出自启、意外退出重启、定时备份与被删除后的恢复。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, GuardFiles.MutexName, out var created);
        if (!created) return 0;

        var directory = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : GuardFiles.DefaultDirectory();
        using var runtime = new GuardRuntime(directory);
        // 注销或关机时不再拉起 ClassIsland，否则会和系统关机抢进程。
        SystemEvents.SessionEnding += (_, _) => runtime.StopForSessionEnd();
        Application.ApplicationExit += (_, _) => runtime.StopForSessionEnd();
        using var context = new ApplicationContext();
        runtime.Exited += () => context.ExitThread();
        runtime.Start();
        Application.Run(context);
        return 0;
    }
}

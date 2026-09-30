using System.Diagnostics;

namespace RemoteCI.Server.Services;

public static class ApplicationRestartCoordinator
{
    private const string HelperSwitch = "--remoteci-restart-helper";

    public static async Task<bool> TryRunHelperAsync(string[] args)
    {
        var index = Array.IndexOf(args, HelperSwitch);
        if (index < 0) return false;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var parentPid)) return true;
        try { using var parent = Process.GetProcessById(parentPid); await parent.WaitForExitAsync(); } catch (ArgumentException) { }
        await Task.Delay(500);
        StartCurrentApplication(args[..index]);
        return true;
    }

    public static void ScheduleRestart(IHostApplicationLifetime lifetime, IHostEnvironment environment)
    {
        var externallyManaged = UpdateService.IsFnosRuntime || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        if (!externallyManaged)
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            StartCurrentApplication(args.Concat([HelperSwitch, Environment.ProcessId.ToString()]).ToArray());
        }
        _ = Task.Run(async () => { await Task.Delay(1500); lifetime.StopApplication(); });
    }

    private static void StartCurrentApplication(IEnumerable<string> args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process.Start(start);
    }
}

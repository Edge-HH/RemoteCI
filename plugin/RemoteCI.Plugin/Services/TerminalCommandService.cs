using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 远程终端：在设备上以 cmd /d /c 执行单条命令并取回标准输出与标准错误。
/// 每次执行相互独立，不保留 shell 状态；输出与时长都有硬上限，
/// 保证回执能落在服务端 15 秒命令窗口内。
/// </summary>
public sealed class TerminalCommandService
{
    /// <summary>控制台程序按 OEM 代码页输出（中文 Windows 为 GBK）；初始化失败时退回 UTF-8。</summary>
    private static readonly Encoding ConsoleEncoding = CreateConsoleEncoding();

    private readonly ILogger _logger;

    public TerminalCommandService(ILogger<TerminalCommandService>? logger = null) =>
        _logger = logger ?? NullLogger<TerminalCommandService>.Instance;

    public async Task<CommandResult> ExecuteAsync(TerminalCommandRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Command))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少终端命令");
        var command = request.Command.Trim();
        if (command.Length > TerminalCommandRequest.MaxCommandLength)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "终端命令过长");
        var timeoutSeconds = Math.Clamp(
            request.TimeoutSeconds ?? TerminalCommandRequest.DefaultTimeoutSeconds,
            1, TerminalCommandRequest.MaxTimeoutSeconds);
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "工作目录不存在");

        var start = new ProcessStartInfo
        {
            // 命令文本本身就是 shell 语法，必须原样交给 cmd 解析，因此使用 Arguments 而非 ArgumentList。
            FileName = "cmd.exe",
            Arguments = $"/d /c {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "远程终端无法启动 cmd.exe");
            return CommandResult.Failure(CommandResultCodes.InternalError, "无法启动命令进程");
        }

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var (partial, _) = await CombineAsync(stdout, stderr);
            return new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.Timeout,
                Message = $"命令执行超过 {timeoutSeconds} 秒，已强制结束进程",
                Data = AppendNote(partial, "输出可能不完整"),
            };
        }

        var (output, truncated) = await CombineAsync(stdout, stderr);
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = process.ExitCode == 0
                ? "命令已执行完成"
                : $"命令已执行，退出代码 {process.ExitCode}",
            Data = truncated ? AppendNote(output, "输出过长，已截断") : output,
        };
    }

    /// <summary>解析并校验工作目录；缺省使用设备用户目录。</summary>
    private static string? ResolveWorkingDirectory(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trimmed = requested.Trim();
        if (trimmed.Length > TerminalCommandRequest.MaxWorkingDirectoryLength) return null;
        return Directory.Exists(trimmed) ? trimmed : null;
    }

    private static Encoding CreateConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    /// <summary>读取输出并在上限处截断；截断后仍继续排空管道，避免子进程写阻塞拖满整个超时窗口。</summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length < TerminalCommandRequest.MaxOutputChars)
                builder.Append(buffer, 0, Math.Min(read, TerminalCommandRequest.MaxOutputChars - builder.Length));
        }
        return (builder.ToString(), builder.Length >= TerminalCommandRequest.MaxOutputChars);
    }

    private static async Task<(string Text, bool Truncated)> CombineAsync(
        Task<(string Text, bool Truncated)> stdout, Task<(string Text, bool Truncated)> stderr)
    {
        var (outText, outTruncated) = await stdout;
        var (errText, errTruncated) = await stderr;
        outText = outText.TrimEnd();
        errText = errText.TrimEnd();
        var combined = errText.Length == 0
            ? outText
            : $"{outText}\n--- 标准错误 ---\n{errText}".TrimStart();
        return (combined, outTruncated || errTruncated);
    }

    private static string AppendNote(string output, string note) =>
        string.IsNullOrWhiteSpace(output) ? $"（{note}）" : $"{output}\n（{note}）";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // 进程恰好在检查与终止之间退出属正常竞态。
        }
    }
}

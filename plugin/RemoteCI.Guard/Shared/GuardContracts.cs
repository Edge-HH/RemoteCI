using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RemoteCI.Guard;

// 插件（.NET 8）与守护进程（.NET Framework 4.8）之间的文件契约。本文件被两个项目共同编译，
// 只能使用两边都有的 API；序列化统一用 DataContractJsonSerializer。

/// <summary>守护目录中的固定文件名。</summary>
internal static class GuardFiles
{
    public const string ExecutableName = "RemoteCI.Guard.exe";
    public const string ProcessName = "RemoteCI.Guard";
    public const string Policy = "policy.json";
    public const string State = "state.json";
    public const string Status = "guard-status.json";
    public const string PendingNotice = "pending-notice.json";
    public const string Events = "events.jsonl";
    public const string EventsSending = "events.sending.jsonl";
    public const string Log = "guard.log";
    public const string BackupFolder = "backup";
    public const string BackupCurrent = "current";
    public const string BackupManifest = "manifest.json";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "RemoteCI.Guard";
    public const string MutexName = @"Local\RemoteCI.Guard";

    /// <summary>%LOCALAPPDATA%\RemoteCI\Guard：位于 C 盘当前用户目录，无需管理员权限，普通用户也不易看到。</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCI", "Guard");
}

/// <summary>ClassIsland 的运行标记，由插件写入：running 启动后、exiting 正常退出前、updating 升级前。</summary>
internal static class GuardRunStates
{
    public const string Running = "running";
    public const string Exiting = "exiting";
    public const string Updating = "updating";
}

/// <summary>插件写给守护进程的配置（policy.json）。</summary>
[DataContract]
internal sealed class GuardConfig
{
    [DataMember(Name = "enabled")] public bool Enabled { get; set; }
    [DataMember(Name = "restartAfterExit")] public bool RestartAfterExit { get; set; }
    [DataMember(Name = "restartDelayMinutes")] public int RestartDelayMinutes { get; set; }
    [DataMember(Name = "restartOnCrash")] public bool RestartOnCrash { get; set; }
    [DataMember(Name = "preventDeletion")] public bool PreventDeletion { get; set; }
    [DataMember(Name = "backupIntervalHours")] public int BackupIntervalHours { get; set; } = 24;
    [DataMember(Name = "organizationName")] public string? OrganizationName { get; set; }

    /// <summary>启动 ClassIsland 使用的程序（优先启动器 ClassIsland.exe）。</summary>
    [DataMember(Name = "launchPath")] public string LaunchPath { get; set; } = string.Empty;

    /// <summary>ClassIsland 安装根目录；该目录下的任何进程都视为 ClassIsland 正在运行（含自带更新器）。</summary>
    [DataMember(Name = "packageRoot")] public string PackageRoot { get; set; } = string.Empty;

    /// <summary>任一缺失即视为 ClassIsland 被删除（启动器与实际运行的程序）。</summary>
    [DataMember(Name = "sentinels")] public List<string> Sentinels { get; set; } = new();

    /// <summary>需要备份与恢复的目录；第一项是安装根目录，其余是位于安装目录之外的数据目录。</summary>
    [DataMember(Name = "backupRoots")] public List<string> BackupRoots { get; set; } = new();

    /// <summary>不备份的目录（日志、缓存、临时文件等），使用完整路径。</summary>
    [DataMember(Name = "excludes")] public List<string> Excludes { get; set; } = new();

    [DataMember(Name = "classIslandVersion")] public string? ClassIslandVersion { get; set; }
    [DataMember(Name = "updatedAtUnixMs")] public long UpdatedAtUnixMs { get; set; }
}

/// <summary>插件写入的运行标记（state.json）。</summary>
[DataContract]
internal sealed class GuardRunState
{
    [DataMember(Name = "pid")] public int Pid { get; set; }
    [DataMember(Name = "state")] public string State { get; set; } = GuardRunStates.Running;
    [DataMember(Name = "atUnixMs")] public long AtUnixMs { get; set; }
}

/// <summary>守护进程恢复 ClassIsland 后留下的提示（pending-notice.json），插件在新实例里弹出原生对话框后删除。</summary>
[DataContract]
internal sealed class GuardNotice
{
    [DataMember(Name = "kind")] public string Kind { get; set; } = "restored";
    [DataMember(Name = "atUnixMs")] public long AtUnixMs { get; set; }
}

/// <summary>events.jsonl 中的一行，字段与服务端 AppGuardEvent 一致。</summary>
[DataContract]
internal sealed class GuardEventLine
{
    [DataMember(Name = "kind")] public string Kind { get; set; } = string.Empty;
    [DataMember(Name = "atUnixMs")] public long AtUnixMs { get; set; }
    [DataMember(Name = "detail", EmitDefaultValue = false)] public string? Detail { get; set; }
}

/// <summary>守护进程心跳与最近一次备份情况（guard-status.json），供插件设置页显示。</summary>
[DataContract]
internal sealed class GuardStatus
{
    [DataMember(Name = "pid")] public int Pid { get; set; }
    [DataMember(Name = "version")] public string? Version { get; set; }
    [DataMember(Name = "heartbeatUnixMs")] public long HeartbeatUnixMs { get; set; }
    [DataMember(Name = "lastBackupUnixMs")] public long LastBackupUnixMs { get; set; }
    [DataMember(Name = "lastBackupBytes")] public long LastBackupBytes { get; set; }
    [DataMember(Name = "lastError", EmitDefaultValue = false)] public string? LastError { get; set; }
}

/// <summary>备份清单（backup\manifest.json）。</summary>
[DataContract]
internal sealed class GuardBackupManifest
{
    [DataMember(Name = "roots")] public List<string> Roots { get; set; } = new();
    [DataMember(Name = "classIslandVersion")] public string? ClassIslandVersion { get; set; }
    [DataMember(Name = "createdAtUnixMs")] public long CreatedAtUnixMs { get; set; }
    [DataMember(Name = "totalBytes")] public long TotalBytes { get; set; }
    [DataMember(Name = "fileCount")] public int FileCount { get; set; }
}

internal static class GuardJson
{
    public static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return serializer.ReadObject(stream) as T;
        }
        catch (SerializationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static T? ReadFile<T>(string path) where T : class
    {
        try { return File.Exists(path) ? Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>先写临时文件再替换，避免另一方读到半截内容。</summary>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

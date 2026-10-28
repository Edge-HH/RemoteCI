using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace RemoteCI.Guard;

internal sealed class GuardBackupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public int CopiedFiles { get; set; }
    public int SkippedFiles { get; set; }
}

/// <summary>
/// 把 ClassIsland 安装目录（及安装目录外的数据目录）镜像到守护目录，并在被删除后只补回缺失的文件。
/// 备份是增量镜像：大小或修改时间不同才复制；源目录里明显少了大量文件时放弃本次备份，避免把正在被删除的状态同步进备份。
/// </summary>
internal static class GuardBackup
{
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>备份中已有超过该数量的文件时才启用“大量缺失”保护，避免首次或很小的目录误判。</summary>
    public const int MassDeletionMinFiles = 50;
    public const double MassDeletionRatio = 0.3;
    private const int CopyAttempts = 3;

    public static string CurrentDirectory(string guardDirectory) =>
        Path.Combine(guardDirectory, GuardFiles.BackupFolder, GuardFiles.BackupCurrent);

    public static string ManifestPath(string guardDirectory) =>
        Path.Combine(guardDirectory, GuardFiles.BackupFolder, GuardFiles.BackupManifest);

    public static string ProtectedBackupRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCI", "ProtectedBackups");

    public static void ConfigureProtectedBackup(
        GuardConfig config, bool enabled, Action<string>? log, string? protectedRoot = null)
    {
        var root = NormalizeDirectory(protectedRoot ?? ProtectedBackupRoot());
        if (!enabled)
        {
            RemoveProtectedBackup(config.ProtectedBackupPath, root, log);
            config.RandomBackupPath = false;
            config.ProtectedBackupPath = null;
            config.ProtectedBackupReady = false;
            return;
        }
        config.RandomBackupPath = true;
        config.BackupDirectoryName = GuardFiles.NormalizeBackupDirectoryName(config.BackupDirectoryName);
        var directoryName = config.BackupDirectoryName.Length > 0
            ? config.BackupDirectoryName
            : Guid.NewGuid().ToString("N");
        var currentName = Path.GetFileName(config.ProtectedBackupPath);
        var hasRandomName = Guid.TryParseExact(currentName, "N", out _);
        if (!IsProtectedDirectory(config.ProtectedBackupPath, root) ||
            (config.BackupDirectoryName.Length > 0 &&
             !string.Equals(currentName, directoryName, StringComparison.OrdinalIgnoreCase)) ||
            (config.BackupDirectoryName.Length == 0 && !hasRandomName))
        {
            RemoveProtectedBackup(config.ProtectedBackupPath, root, log);
            config.ProtectedBackupPath = Path.Combine(root, directoryName);
        }
        try
        {
            Directory.CreateDirectory(config.ProtectedBackupPath!);
#if NET8_0_OR_GREATER
            if (OperatingSystem.IsWindows()) RestrictProtectedDirectory(config.ProtectedBackupPath!);
#else
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) RestrictProtectedDirectory(config.ProtectedBackupPath!);
#endif
            config.ProtectedBackupReady = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            config.ProtectedBackupReady = false;
            log?.Invoke($"安全备份目录不可用：{ex.Message}");
        }
    }

    private static bool IsProtectedDirectory(string? path, string root) =>
        root.Length > 0 && !string.IsNullOrWhiteSpace(path) &&
        !string.Equals(NormalizeDirectory(path), root, StringComparison.OrdinalIgnoreCase) &&
        IsUnder(path!, root);

    private static void RemoveProtectedBackup(string? path, string root, Action<string>? log)
    {
        if (!IsProtectedDirectory(path, root)) return;
        try
        {
            var backup = Path.Combine(path!, "RemoteCI.Guard.Backup");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"清理随机安全备份失败：{ex.Message}");
        }
    }

#if NET8_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    private static void RestrictProtectedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                     .OfType<FileSystemAccessRule>().ToList())
            security.RemoveAccessRule(rule);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null)
            security.AddAccessRule(new FileSystemAccessRule(
                current, FileSystemRights.Modify, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    public static GuardBackupResult Mirror(GuardConfig config, string guardDirectory, Action<string> log)
    {
        var aggregate = new GuardBackupResult();
        var errors = new List<string>();
        foreach (var target in BackupTargets(config, guardDirectory, log))
        {
            var result = MirrorTarget(config, target, log);
            aggregate.CopiedFiles += result.CopiedFiles;
            aggregate.SkippedFiles += result.SkippedFiles;
            if (!result.Success)
            {
                errors.Add($"{target}：{result.Error}");
                continue;
            }
            aggregate.Success = true;
            aggregate.TotalBytes = result.TotalBytes;
            aggregate.FileCount = result.FileCount;
        }
        if (!aggregate.Success) aggregate.Error = errors.Count > 0 ? string.Join("；", errors) : "没有可用的备份目标";
        else if (errors.Count > 0) log($"部分备份目标失败：{string.Join("；", errors)}");
        return aggregate;
    }

    private static GuardBackupResult MirrorTarget(GuardConfig config, string backupDirectory, Action<string> log)
    {
        var result = new GuardBackupResult();
        var roots = NormalizeRoots(config.BackupRoots);
        if (roots.Count == 0 || !Directory.Exists(roots[0]))
            return Fail(result, "ClassIsland 安装目录不存在，无法备份");
        var excludes = config.Excludes.Select(NormalizeDirectory).Where(x => x.Length > 0).ToList();
        var guardRoot = NormalizeDirectory(backupDirectory);
        var plans = new List<(string Source, string Target, List<FileInfo> Files)>();
        for (var i = 0; i < roots.Count; i++)
        {
            if (!Directory.Exists(roots[i])) continue;
            var files = new List<FileInfo>();
            Collect(new DirectoryInfo(roots[i]), excludes, guardRoot, files);
            plans.Add((roots[i], Path.Combine(CurrentDirectory(backupDirectory), i.ToString()), files));
            result.TotalBytes += files.Sum(x => x.Length);
            result.FileCount += files.Count;
        }
        if (result.TotalBytes > MaxTotalBytes)
            return Fail(result, $"需要备份的文件共 {result.TotalBytes / 1024 / 1024} MB，超过 {MaxTotalBytes / 1024 / 1024} MB 上限，已跳过");
        foreach (var plan in plans)
        {
            var existing = Directory.Exists(plan.Target) ? ListRelative(plan.Target) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceRelative = new HashSet<string>(plan.Files.Select(x => Relative(plan.Source, x.FullName)), StringComparer.OrdinalIgnoreCase);
            var removed = existing.Where(x => !sourceRelative.Contains(x)).ToList();
            if (existing.Count >= MassDeletionMinFiles && removed.Count > existing.Count * MassDeletionRatio)
                return Fail(result, $"{plan.Source} 比上次备份少了 {removed.Count} 个文件，疑似正在被删除，已跳过本次备份");
        }
        foreach (var plan in plans)
        {
            Directory.CreateDirectory(plan.Target);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.Files)
            {
                var relative = Relative(plan.Source, file.FullName);
                keep.Add(relative);
                var target = new FileInfo(Path.Combine(plan.Target, relative));
                if (target.Exists && target.Length == file.Length && target.LastWriteTimeUtc == file.LastWriteTimeUtc) continue;
                if (TryCopy(file.FullName, target.FullName, file.LastWriteTimeUtc)) result.CopiedFiles++;
                else { result.SkippedFiles++; log($"备份跳过被占用的文件：{file.FullName}"); }
            }
            foreach (var stale in ListRelative(plan.Target).Where(x => !keep.Contains(x)))
            {
                try { File.Delete(Path.Combine(plan.Target, stale)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            RemoveEmptyDirectories(plan.Target);
        }
        GuardJson.WriteFile(ManifestPath(backupDirectory), new GuardBackupManifest
        {
            Roots = roots,
            ClassIslandVersion = config.ClassIslandVersion,
            CreatedAtUnixMs = GuardJson.NowUnixMs(),
            TotalBytes = result.TotalBytes,
            FileCount = result.FileCount,
        });
        result.Success = true;
        return result;
    }

    /// <summary>按清单时间逐个尝试副本，只补回缺失文件，不覆盖现存文件。</summary>
    public static GuardBackupResult Restore(GuardConfig config, string guardDirectory, Action<string> log)
    {
        var errors = new List<string>();
        var targets = BackupTargets(config, guardDirectory, log)
            .Select(x => new { Directory = x, Manifest = GuardJson.ReadFile<GuardBackupManifest>(ManifestPath(x)) })
            .OrderByDescending(x => x.Manifest?.CreatedAtUnixMs ?? 0)
            .ToList();
        foreach (var target in targets)
        {
            var result = RestoreTarget(config, target.Directory, target.Manifest, log);
            if (result.Success) return result;
            errors.Add($"{target.Directory}：{result.Error}");
        }
        return Fail(new GuardBackupResult(), errors.Count > 0 ? string.Join("；", errors) : "没有可用的备份");
    }

    private static GuardBackupResult RestoreTarget(GuardConfig config, string backupDirectory, GuardBackupManifest? manifest, Action<string> log)
    {
        var result = new GuardBackupResult();
        if (manifest is null || manifest.Roots.Count == 0) return Fail(result, "没有可用的备份");
        for (var i = 0; i < manifest.Roots.Count; i++)
        {
            var source = Path.Combine(CurrentDirectory(backupDirectory), i.ToString());
            if (!Directory.Exists(source)) continue;
            var target = manifest.Roots[i];
            foreach (var relative in ListRelative(source))
            {
                result.FileCount++;
                var destination = Path.Combine(target, relative);
                if (File.Exists(destination)) continue;
                var from = new FileInfo(Path.Combine(source, relative));
                if (TryCopy(from.FullName, destination, from.LastWriteTimeUtc))
                {
                    result.CopiedFiles++;
                    result.TotalBytes += from.Length;
                }
                else { result.SkippedFiles++; log($"恢复失败的文件：{destination}"); }
            }
        }
        if (result.FileCount == 0) return Fail(result, "备份为空");
        var stillMissing = config.Sentinels.Where(x => !string.IsNullOrWhiteSpace(x) && !File.Exists(x)).ToList();
        if (stillMissing.Count > 0) return Fail(result, $"恢复后仍缺少 {string.Join("、", stillMissing)}");
        result.Success = true;
        return result;
    }

    /// <summary>任一目标从未备份、配置或版本变化、超过间隔时执行一轮备份。</summary>
    public static bool IsDue(GuardConfig config, GuardBackupManifest? manifest, long lastAttemptMs, long nowMs)
    {
        if (lastAttemptMs > 0 && nowMs - lastAttemptMs < 60 * 60_000L) return false;
        var roots = NormalizeRoots(config.BackupRoots);
        var interval = Math.Max(1, config.BackupIntervalHours) * 60L * 60_000L;
        if (IsTargetDue(manifest, null)) return true;
        foreach (var target in BackupTargets(config, string.Empty, null).Where(x => x.Length > 0))
        {
            var current = GuardJson.ReadFile<GuardBackupManifest>(ManifestPath(target));
            if (IsTargetDue(current, target)) return true;
        }
        return false;

        bool IsTargetDue(GuardBackupManifest? current, string? target)
        {
            if (current is null || !roots.SequenceEqual(current.Roots, StringComparer.OrdinalIgnoreCase) ||
                nowMs - current.CreatedAtUnixMs >= interval) return true;
            return !string.IsNullOrEmpty(config.ClassIslandVersion) &&
                !string.Equals(config.ClassIslandVersion, current.ClassIslandVersion, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static List<string> BackupTargets(GuardConfig config, string guardDirectory, Action<string>? log)
    {
        var targets = new List<string> { guardDirectory };
        var configuredPaths = config.BackupPaths ?? new List<string>();
        if (config.RandomBackupPath && config.ProtectedBackupReady && !string.IsNullOrWhiteSpace(config.ProtectedBackupPath))
            configuredPaths = new[] { config.ProtectedBackupPath! }.Concat(configuredPaths).ToList();
        foreach (var configured in configuredPaths)
        {
            try
            {
                if (!Path.IsPathRooted(configured)) throw new ArgumentException("必须使用绝对路径");
                var target = Path.GetFullPath(Path.Combine(configured, "RemoteCI.Guard.Backup"));
                if (config.BackupRoots.Any(root => IsUnder(target, root) || IsUnder(root, target)))
                    throw new ArgumentException("备份目标不能与 ClassIsland 源目录互相嵌套");
                if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase)) targets.Add(target);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                log?.Invoke($"忽略无效备份路径 {configured}：{ex.Message}");
            }
        }
        return targets;
    }

    public static bool IsUnder(string path, string directory)
    {
        var root = NormalizeDirectory(directory);
        if (root.Length == 0) return false;
        var full = NormalizeDirectory(path);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeRoots(IEnumerable<string> roots) =>
        roots.Select(NormalizeDirectory).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }

    private static void Collect(DirectoryInfo directory, List<string> excludes, string guardRoot, List<FileInfo> files)
    {
        var path = NormalizeDirectory(directory.FullName);
        if (excludes.Any(x => IsUnder(path, x)) || (guardRoot.Length > 0 && IsUnder(path, guardRoot))) return;
        try
        {
            foreach (var file in directory.GetFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(file);
            }
            foreach (var child in directory.GetDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                Collect(child, excludes, guardRoot, files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static HashSet<string> ListRelative(string root)
    {
        var files = new List<FileInfo>();
        Collect(new DirectoryInfo(root), new List<string>(), string.Empty, files);
        return new HashSet<string>(files.Select(x => Relative(root, x.FullName)), StringComparer.OrdinalIgnoreCase);
    }

    private static string Relative(string root, string path) =>
        path.Substring(NormalizeDirectory(root).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool TryCopy(string source, string destination, DateTime lastWriteUtc)
    {
        for (var attempt = 1; attempt <= CopyAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".rcitmp";
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.SetLastWriteTimeUtc(temporary, lastWriteUtc);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < CopyAttempts) Thread.Sleep(300);
            }
        }
        try { File.Delete(destination + ".rcitmp"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var child in Directory.GetDirectories(root))
            {
                RemoveEmptyDirectories(child);
                if (Directory.GetFileSystemEntries(child).Length == 0) Directory.Delete(child);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static GuardBackupResult Fail(GuardBackupResult result, string error)
    {
        result.Success = false;
        result.Error = error;
        return result;
    }
}

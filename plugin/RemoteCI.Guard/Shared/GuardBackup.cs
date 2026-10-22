using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public static GuardBackupResult Mirror(GuardConfig config, string guardDirectory, Action<string> log)
    {
        var result = new GuardBackupResult();
        var roots = NormalizeRoots(config.BackupRoots);
        if (roots.Count == 0 || !Directory.Exists(roots[0]))
            return Fail(result, "ClassIsland 安装目录不存在，无法备份");
        var excludes = config.Excludes.Select(NormalizeDirectory).Where(x => x.Length > 0).ToList();
        var guardRoot = NormalizeDirectory(guardDirectory);

        var plans = new List<(string Source, string Target, List<FileInfo> Files)>();
        for (var i = 0; i < roots.Count; i++)
        {
            if (!Directory.Exists(roots[i])) continue;
            var files = new List<FileInfo>();
            Collect(new DirectoryInfo(roots[i]), excludes, guardRoot, files);
            plans.Add((roots[i], Path.Combine(CurrentDirectory(guardDirectory), i.ToString()), files));
            result.TotalBytes += files.Sum(x => x.Length);
            result.FileCount += files.Count;
        }
        if (result.TotalBytes > MaxTotalBytes)
            return Fail(result, $"需要备份的文件共 {result.TotalBytes / 1024 / 1024} MB，超过 {MaxTotalBytes / 1024 / 1024} MB 上限，已跳过");

        foreach (var plan in plans)
        {
            var existing = Directory.Exists(plan.Target)
                ? ListRelative(plan.Target)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                else
                {
                    result.SkippedFiles++;
                    log($"备份跳过被占用的文件：{file.FullName}");
                }
            }
            foreach (var stale in ListRelative(plan.Target).Where(x => !keep.Contains(x)))
            {
                try { File.Delete(Path.Combine(plan.Target, stale)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            RemoveEmptyDirectories(plan.Target);
        }

        GuardJson.WriteFile(ManifestPath(guardDirectory), new GuardBackupManifest
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

    /// <summary>只补回缺失的文件，不覆盖仍存在的文件（可能是被删除前刚改过的配置），也不删除多出来的文件。</summary>
    public static GuardBackupResult Restore(GuardConfig config, string guardDirectory, Action<string> log)
    {
        var result = new GuardBackupResult();
        var manifest = GuardJson.ReadFile<GuardBackupManifest>(ManifestPath(guardDirectory));
        if (manifest is null || manifest.Roots.Count == 0)
            return Fail(result, "没有可用的备份");
        for (var i = 0; i < manifest.Roots.Count; i++)
        {
            var source = Path.Combine(CurrentDirectory(guardDirectory), i.ToString());
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
                else
                {
                    result.SkippedFiles++;
                    log($"恢复失败的文件：{destination}");
                }
            }
        }
        if (result.FileCount == 0) return Fail(result, "备份为空");
        var stillMissing = config.Sentinels.Where(x => !string.IsNullOrWhiteSpace(x) && !File.Exists(x)).ToList();
        if (stillMissing.Count > 0) return Fail(result, $"恢复后仍缺少 {string.Join("、", stillMissing)}");
        result.Success = true;
        return result;
    }

    /// <summary>备份是否到期：从未备份、超过间隔，或 ClassIsland 版本与上次备份不同。</summary>
    public static bool IsDue(GuardConfig config, GuardBackupManifest? manifest, long lastAttemptMs, long nowMs)
    {
        // 失败或跳过后至少隔一小时再试。
        if (lastAttemptMs > 0 && nowMs - lastAttemptMs < 60 * 60_000L) return false;
        if (manifest is null) return true;
        if (!string.IsNullOrEmpty(config.ClassIslandVersion) &&
            !string.Equals(config.ClassIslandVersion, manifest.ClassIslandVersion, StringComparison.OrdinalIgnoreCase)) return true;
        var roots = NormalizeRoots(config.BackupRoots);
        if (!roots.SequenceEqual(manifest.Roots, StringComparer.OrdinalIgnoreCase)) return true;
        var interval = Math.Max(1, config.BackupIntervalHours) * 60L * 60_000L;
        return nowMs - manifest.CreatedAtUnixMs >= interval;
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

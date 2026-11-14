// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    public class CleanCandidate
    {
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string SizeText => FormatSize(SizeBytes);
        public DateTime LastWrite { get; set; }
        public string Category { get; set; } = "";

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }
    }

    public class CleanResult
    {
        public bool DryRun { get; set; }
        public int Requested { get; set; }
        public int Deleted { get; set; }
        public int Skipped { get; set; }
        public long FreedBytes { get; set; }
        public string FreedText => CleanCandidate.FormatSize(FreedBytes);
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// 磁盘清理：只扫描白名单内的临时目录，删除时再次校验路径归属，
    /// 默认走回收站且默认 dry-run，避免误删用户文件。
    /// </summary>
    public class DiskCleanerService
    {
        private const int MaxScanFiles = 3000;

        /// <summary>单个扫描根目录的最大候选文件数。各根目录独立计数，避免系统 Temp 文件极多时占满全局上限、挤掉应用目录。</summary>
        private const int MaxPerRoot = 500;

        /// <summary>允许扫描（也只允许删除）的根目录。</summary>
        public List<string> AllowedRoots()
        {
            var roots = new List<string>();
            AddSafe(roots, Path.GetTempPath());
            AddSafe(roots, Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Temp"));
            AddSafe(roots, SettingsStore.Instance.TempDir);
            AddSafe(roots, SettingsStore.Instance.LogDir);
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddSafe(List<string> list, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full)) list.Add(full.TrimEnd(Path.DirectorySeparatorChar));
            }
            catch { /* 忽略非法路径 */ }
        }

        private List<string> ProtectedPaths()
        {
            var list = new List<string>();
            foreach (var raw in SettingsStore.Instance.Current.CleanWhitelist ?? new List<string>())
            {
                try
                {
                    var p = Environment.ExpandEnvironmentVariables(raw);
                    if (!string.IsNullOrWhiteSpace(p))
                        list.Add(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar));
                }
                catch { /* 忽略非法配置 */ }
            }
            return list;
        }

        private bool IsProtected(string fullPath, List<string> protectedPaths)
        {
            foreach (var p in protectedPaths)
            {
                if (fullPath.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool IsUnderAllowedRoot(string fullPath)
        {
            foreach (var root in AllowedRoots())
            {
                if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>扫描可回收的临时文件与大文件。</summary>
        public List<CleanCandidate> Scan(int maxAgeDays, int minSizeMB)
        {
            var result = new List<CleanCandidate>();
            var protectedPaths = ProtectedPaths();
            var cutoff = DateTime.Now.AddDays(-Math.Max(0, maxAgeDays));
            var minBytes = (long)Math.Max(0, minSizeMB) * 1024 * 1024;

            foreach (var root in AllowedRoots())
            {
                var category = root.EndsWith("log", StringComparison.OrdinalIgnoreCase)
                    ? "应用日志" : "临时文件";
                var rootCount = 0;
                try
                {
                    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        // 每根目录独立配额：超过即跳过该根，不挤占其他目录的份额
                        if (rootCount >= MaxPerRoot) break;
                        try
                        {
                            var full = Path.GetFullPath(file);
                            if (IsProtected(full, protectedPaths)) continue;
                            if (!IsUnderAllowedRoot(full)) continue;

                            var fi = new FileInfo(full);
                            if (!fi.Exists || fi.Length < minBytes) continue;
                            // 临时文件按过期时间过滤；日志目录始终按过期过滤
                            if (fi.LastWriteTime > cutoff) continue;

                            result.Add(new CleanCandidate
                            {
                                Path = full,
                                SizeBytes = fi.Length,
                                LastWrite = fi.LastWriteTime,
                                Category = category
                            });
                            rootCount++;
                        }
                        catch { /* 单个文件无权限时跳过 */ }
                    }
                }
                catch (Exception ex)
                {
                    Tools.Logger("扫描目录失败 " + root + ": " + ex.Message);
                }
            }
            return result.OrderByDescending(x => x.SizeBytes).ToList();
        }

        /// <summary>
        /// 启动时自动清理应用自产临时文件（temp/）。删除超过 maxAgeDays 天的文件，
        /// 只作用于应用目录，不碰系统 Temp；单文件锁失败静默跳过，不影响启动。
        /// </summary>
        public int AutoCleanAppTemp(int maxAgeDays)
        {
            var deleted = 0;
            var cutoff = DateTime.Now.AddDays(-maxAgeDays);
            try
            {
                var tempDir = SettingsStore.Instance.TempDir;
                if (!Directory.Exists(tempDir)) return 0;
                foreach (var f in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!fi.Exists || fi.LastWriteTime > cutoff) continue;
                        File.Delete(f);
                        deleted++;
                    }
                    catch { /* 单文件被占用或无权限时跳过，不影响其余清理 */ }
                }
                if (deleted > 0)
                    Tools.Logger("启动自动清理：应用临时文件删除 " + deleted + " 个（超过 " + maxAgeDays + " 天）");
            }
            catch { /* 目录不可读等整体异常时放弃，不影响启动 */ }
            return deleted;
        }

        /// <summary>
        /// 删除指定文件。dryRun=true 时只做校验与统计，不实际删除。
        /// </summary>
        public CleanResult Clean(IEnumerable<string> paths, bool dryRun, bool useRecycleBin)
        {
            var res = new CleanResult { DryRun = dryRun };
            var protectedPaths = ProtectedPaths();
            var items = (paths ?? Enumerable.Empty<string>()).ToList();
            res.Requested = items.Count;

            foreach (var raw in items)
            {
                try
                {
                    var full = Path.GetFullPath(raw);
                    if (IsProtected(full, protectedPaths) || !IsUnderAllowedRoot(full) || !File.Exists(full))
                    {
                        res.Skipped++;
                        continue;
                    }
                    var size = new FileInfo(full).Length;
                    if (!dryRun)
                    {
                        if (useRecycleBin) SendToRecycleBin(full);
                        else File.Delete(full);
                    }
                    res.Deleted++;
                    res.FreedBytes += size;
                }
                catch (Exception ex)
                {
                    res.Errors.Add(raw + " → " + ex.Message);
                }
            }
            return res;
        }

        private static void SendToRecycleBin(string full)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }

        /// <summary>本机磁盘容量概览。</summary>
        public object GetDiskInfo()
        {
            var drives = new List<object>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    drives.Add(new
                    {
                        name = d.Name,
                        label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel,
                        totalBytes = d.TotalSize,
                        freeBytes = d.AvailableFreeSpace,
                        total = CleanCandidate.FormatSize(d.TotalSize),
                        free = CleanCandidate.FormatSize(d.AvailableFreeSpace),
                        usedPercent = d.TotalSize == 0 ? 0 :
                            Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, 1)
                    });
                }
                catch { /* 光驱等未就绪设备跳过 */ }
            }
            return new { drives, scannedRoots = AllowedRoots() };
        }
    }
}

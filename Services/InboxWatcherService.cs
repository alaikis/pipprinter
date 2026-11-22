// Copyright (c) 2026 Alaikis.com Inc
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
    /// <summary>扫描收件箱中的文件。</summary>
    public class InboxFile
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string Extension { get; set; } = "";
    }

    /// <summary>
    /// 多品牌 MFD 扫描接入（Scan-to-Print）：监听 data/inbox 目录，
    /// 扫描仪/复合机配置为「扫描到共享文件夹」后把文件推送到该目录，服务自动入队打印。
    /// 不内置任何厂商 SDK，兼容所有支持 SMB/SFTP/FTP 落盘的扫描设备（由用户配置扫描目标）。
    /// 默认不开启（配置 InboxWatcherEnabled=true）；新文件需稳定后再入队（防半写文件）。
    /// </summary>
    public static class InboxWatcherService
    {
        private static FileSystemWatcher _watcher;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Recent = new HashSet<string>();
        private static readonly string[] SupportedExt =
            { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".txt", ".xps", ".ofd" };

        public static string InboxDir => Path.Combine(SettingsStore.Instance.DataDir, "inbox");

        public static void Start()
        {
            if (!SettingsStore.Instance.Current.InboxWatcherEnabled) return;
            try
            {
                Directory.CreateDirectory(InboxDir);
                if (_watcher != null) return;
                _watcher = new FileSystemWatcher(InboxDir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    Filter = "*.*"
                };
                _watcher.Created += OnFile;
                _watcher.Renamed += (s, e) => OnFile(s, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath), e.Name));
                _watcher.EnableRaisingEvents = true;
                Tools.Logger("扫描收件目录监听已启动: " + InboxDir);
            }
            catch (Exception ex) { Tools.Logger("扫描收件目录监听启动失败: " + ex.Message); }
        }

        public static void Stop()
        {
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } }
            catch { /* ignore */ }
            _watcher = null;
        }

        private static void OnFile(object sender, FileSystemEventArgs e)
        {
            try
            {
                var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                if (!SupportedExt.Contains(ext)) return;
                lock (Gate) { if (!Recent.Add(e.FullPath)) return; }
                // 延迟 2s 等待文件写完（扫描设备可能分块写入）
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(2000).ConfigureAwait(false);
                    try
                    {
                        if (!File.Exists(e.FullPath)) return;
                        var job = new Models.PrintJob { Source = "file", Target = e.FullPath };
                        PrintQueueService.Instance.Enqueue(job);
                        Tools.Logger("扫描文件自动入队: " + e.Name);
                    }
                    finally { lock (Gate) Recent.Remove(e.FullPath); }
                });
            }
            catch (Exception ex) { Tools.Logger("扫描文件入队失败: " + ex.Message); }
        }

        public static List<InboxFile> ListFiles()
        {
            var list = new List<InboxFile>();
            try
            {
                if (!Directory.Exists(InboxDir)) return list;
                list = Directory.GetFiles(InboxDir)
                    .Where(f => SupportedExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new InboxFile
                    {
                        Name = Path.GetFileName(f),
                        Size = new FileInfo(f).Length,
                        ModifiedAt = File.GetLastWriteTime(f),
                        Extension = Path.GetExtension(f).ToLowerInvariant()
                    })
                    .OrderByDescending(x => x.ModifiedAt).ToList();
            }
            catch (Exception ex) { Tools.Logger("扫描文件列表失败: " + ex.Message); }
            return list;
        }

        /// <summary>按文件名解析收件箱中的文件绝对路径（防目录穿越）。</summary>
        public static string ResolveFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var full = Path.GetFullPath(Path.Combine(InboxDir, name));
                var root = Path.GetFullPath(InboxDir) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }
    }
}

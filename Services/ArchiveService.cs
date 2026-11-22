// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.IO;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 打印存档：把入队任务的原始本地文件复制到 data/archive/{jobId}/，
    /// 供事后审计、失败重放（Retry 从存档读取，避免原文件已删除）。
    /// 网络 URL 任务不落存档体积，仅记录 URL 文本到 manifest.txt。
    /// 复制失败静默，不影响打印。
    /// </summary>
    public static class ArchiveService
    {
        private static string ArchiveRoot => Path.Combine(SettingsStore.Instance.DataDir, "archive");
        private static string JobDir(string jobId) => Path.Combine(ArchiveRoot, jobId);

        public static void ArchiveJob(PrintJob job)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.Id)) return;
            try
            {
                var dir = JobDir(job.Id);
                Directory.CreateDirectory(dir);
                string src = null;
                if (job.Source != "url" && !string.IsNullOrWhiteSpace(job.Target) && File.Exists(job.Target))
                    src = job.Target;

                if (src != null)
                {
                    var ext = Path.GetExtension(src);
                    var dst = Path.Combine(dir, "original" + (string.IsNullOrEmpty(ext) ? "" : ext));
                    File.Copy(src, dst, true);
                    job.ArchivePath = dst;
                }
                File.WriteAllText(Path.Combine(dir, "manifest.txt"),
                    "jobId=" + job.Id + Environment.NewLine +
                    "source=" + (job.Source ?? "") + Environment.NewLine +
                    "target=" + (job.Target ?? "") + Environment.NewLine +
                    "user=" + (job.UserId ?? "") + Environment.NewLine +
                    "enqueuedAt=" + job.EnqueuedAt.ToString("O") + Environment.NewLine +
                    "watermark=" + (job.Watermark ?? "") + Environment.NewLine);
            }
            catch (Exception ex) { Tools.Logger("打印存档失败(不影响打印): " + ex.Message); }
        }

        /// <summary>从存档重放：把存档文件恢复到临时路径并更新 job.Target。返回是否成功。</summary>
        public static bool RestoreForReplay(PrintJob job)
        {
            if (job == null) return false;
            try
            {
                if (string.IsNullOrWhiteSpace(job.ArchivePath) || !File.Exists(job.ArchivePath)) return false;
                var ext = Path.GetExtension(job.ArchivePath);
                var tmp = Path.Combine(Path.GetTempPath(),
                    "litchio-replay-" + job.Id + (string.IsNullOrEmpty(ext) ? "" : ext));
                File.Copy(job.ArchivePath, tmp, true);
                job.Target = tmp;
                job.Source = "file";
                return true;
            }
            catch (Exception ex) { Tools.Logger("存档重放失败: " + ex.Message); return false; }
        }

        /// <summary>清理超过 keepDays 天的存档目录。返回删除目录数。</summary>
        public static int Clean(int keepDays)
        {
            if (keepDays <= 0) return 0;
            int removed = 0;
            try
            {
                if (!Directory.Exists(ArchiveRoot)) return 0;
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var dir in Directory.GetDirectories(ArchiveRoot))
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LastWriteTime < cutoff)
                    {
                        Directory.Delete(dir, true);
                        removed++;
                    }
                }
            }
            catch (Exception ex) { Tools.Logger("存档清理失败: " + ex.Message); }
            return removed;
        }

        public static bool HasArchive(string jobId)
        {
            try { return Directory.Exists(JobDir(jobId)); }
            catch { return false; }
        }

        public static string ArchiveDir(string jobId) => JobDir(jobId);
    }
}

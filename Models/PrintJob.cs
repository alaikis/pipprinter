using System;
using System.IO;

namespace DuoAssistant.Models
{
    /// <summary>打印任务状态（用字符串存储，避免 JSON 枚举转换配置）。</summary>
    public static class JobStatus
    {
        public const string Pending = "pending";
        public const string Printing = "printing";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// 打印队列中的单个任务。持久化到 data/queue.json。
    /// </summary>
    public class PrintJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>file / url / template</summary>
        public string Source { get; set; } = "file";
        /// <summary>本地路径或 URL</summary>
        public string Target { get; set; } = "";
        public string Printer { get; set; } = "";
        public int Copies { get; set; } = 1;
        /// <summary>fit / shrink / noscale / compat</summary>
        public string Fit { get; set; } = "fit";
        /// <summary>portrait / landscape</summary>
        public string Scale { get; set; } = "portrait";
        public string PaperKind { get; set; } = "";
        public string Pages { get; set; } = "1-";
        public string Status { get; set; } = JobStatus.Pending;
        public DateTime EnqueuedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string Error { get; set; } = "";
        public int Attempts { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public string FileName
        {
            get
            {
                try
                {
                    if (Source == "url")
                    {
                        var uri = new Uri(Target);
                        return Path.GetFileName(uri.AbsolutePath);
                    }
                    return Path.GetFileName(Target);
                }
                catch { return string.IsNullOrEmpty(Target) ? "-" : Target; }
            }
        }

        public PrintJob Clone()
        {
            return (PrintJob)MemberwiseClone();
        }
    }
}

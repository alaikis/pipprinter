// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
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
        /// <summary>双面：simplex / duplexlong(长边) / duplexshort(短边)，默认 simplex。</summary>
        public string Duplex { get; set; } = "simplex";
        /// <summary>彩色：null 表示跟随打印机默认；true 彩色，false 黑白。</summary>
        public bool? Color { get; set; } = null;
        /// <summary>逐份（1,2,3,1,2,3），默认 true；false 为 1,1,2,2,3,3。</summary>
        public bool Collate { get; set; } = true;
        /// <summary>四周边距，单位为 1/100 英寸，默认 50（0.5 英寸）。</summary>
        public int Margin { get; set; } = 50;
        /// <summary>纸盒：空表示由打印机自动选择。</summary>
        public string Bin { get; set; } = "";
        /// <summary>DPI：0 表示不指定，仅影响 noscale 图片的打印尺寸。</summary>
        public int Dpi { get; set; } = 0;
        /// <summary>页面奇偶选择：even / odd / 空（全部）。</summary>
        public string PageOrder { get; set; } = "";
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

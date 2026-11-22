// Copyright (c) 2026 Alaikis.com Inc
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
        /// <summary>安全打印：等待认证释放（挂起，不出纸）。</summary>
        public const string Held = "held";
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
        /// <summary>
        /// 幂等键（可选）。调用方提供去重键，或入队时按 源+打印机+参数 自动派生；
        /// 相同键的未完成任务已存在时不再重复入队，防高频场景重复出纸 / 重复计费。
        /// </summary>
        public string IdempotencyKey { get; set; } = "";
        /// <summary>批次组（可选）。同一 groupId 的一批任务按序、可分流多设备，支撑批量面单 / 波次。</summary>
        public string GroupId { get; set; } = "";
        /// <summary>批次内顺序（从 0 起）。同一 group 内按此排序消费。</summary>
        public int GroupOrder { get; set; }
        /// <summary>计划触发时间（UTC）。空=立即；非空且未到点时由排期器到时才入队打印。</summary>
        public DateTime? DueAt { get; set; }
        /// <summary>瞬态：本次 Enqueue 因幂等键命中而未真正新增（调用方拿到的是已存在任务）。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool Deduped { get; set; }

        /// <summary>瞬态：本次解析出的本地文件路径（URL 已下载），供计费页数精确统计。不持久化。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string ResolvedLocalPath { get; set; } = "";

        // ---------------------------------------------------------------- 打印管理（v1.01.011+）
        /// <summary>安全打印：true=任务挂起等待 PIN/工卡认证释放，不自动出纸。</summary>
        public bool ReleaseRequired { get; set; }
        /// <summary>安全打印：认证释放所用 PIN 的 PBKDF2 哈希（salt$hash），不存明文。</summary>
        public string PinHash { get; set; } = "";
        /// <summary>安全打印：释放人标识（PIN/工卡/用户名）。</summary>
        public string ReleasedBy { get; set; } = "";
        public DateTime? ReleasedAt { get; set; }
        /// <summary>任务归属用户（配额/费用核算维度）。空=本机默认用户。</summary>
        public string UserId { get; set; } = "";
        /// <summary>水印文字（打印前叠加到每页）。</summary>
        public string Watermark { get; set; } = "";
        /// <summary>打印偏移微调（毫米，正值为右下）。</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        /// <summary>费用核算：本任务计费用页数（打印成功后写入）。</summary>
        public int BillablePages { get; set; }
        /// <summary>策略引擎：被策略覆写的参数说明（审计用）。</summary>
        public string PolicyApplied { get; set; } = "";
        /// <summary>存档：原始文件在 data/archive/{jobId}/ 下的相对路径。</summary>
        public string ArchivePath { get; set; } = "";

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

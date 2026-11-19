// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 一条打印机别名（slug）记录。
    /// 对接方 / 用户只用 slug（如「前台打印机」）指代一台打印机，
    /// 复杂的目标与协议选择都藏在 Target / Mode 里，对外只暴露一个友好名称。
    /// </summary>
    public class PrinterSlugEntry
    {
        /// <summary>别名本体（对外唯一可见名，如「前台打印机」）。</summary>
        public string Slug { get; set; } = "";

        /// <summary>
        /// 实际目标：
        ///   host:port / 裸 IP        → 走网络直发（NetPrinterService.PrintSmart 自动识别协议）
        ///   Windows 打印机驱动名     → 走 Win32 驱动管线
        ///   留空或 "auto"           → 按 host:port 探测，或回退系统默认
        /// </summary>
        public string Target { get; set; } = "";

        /// <summary>
        /// 协议 / 通道偏好（可选）：auto（默认，自动 631→9100）/ ipp / raw / driver（强制走本机驱动名）。
        /// 只有网络目标下 ipp/raw 才有意义；driver 表示该目标其实是 Windows 已装打印机名。
        /// </summary>
        public string Mode { get; set; } = "auto";

        /// <summary>备注（人类可读，展示用，不参与调度）。</summary>
        public string Note { get; set; } = "";

        /// <summary>创建 / 修改时间戳（Unix 秒）。</summary>
        public long Ts { get; set; }
    }

    /// <summary>
    /// 打印机别名表：把「复杂的目标 + 协议」藏到一个友好 slug 后面。
    /// 持久化到 数据目录/netprint-slug.json（原子写 + 全局锁），
    /// 供打印入口的 SlugResolver 做四级智能调度，也供 UI / 命令做手动刷新与增删。
    /// 纯本地配置，不碰系统注册表、不装驱动、不注册 Windows 假打印机。
    /// </summary>
    public sealed class PrinterSlugStore
    {
        private static readonly object Gate = new object();
        private static readonly string Path =
            System.IO.Path.Combine(SettingsStore.Instance.DataDir, "netprint-slug.json");

        /// <summary>列出全部别名（按 slug 名排序，方便展示）。</summary>
        public static List<PrinterSlugEntry> List()
        {
            lock (Gate)
            {
                var map = ReadMap();
                return map.Values
                    .OrderBy(e => e.Slug, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>按 slug 名取一条；不存在返回 null。</summary>
        public static PrinterSlugEntry Get(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;
            lock (Gate)
            {
                var map = ReadMap();
                if (map.TryGetValue(slug, out var e)) return e;
                return null;
            }
        }

        /// <summary>是否已有某 slug。</summary>
        public static bool Exists(string slug) => Get(slug) != null;

        /// <summary>新增或覆盖一条别名；返回落库后的条目。</summary>
        public static PrinterSlugEntry Upsert(PrinterSlugEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Slug))
                throw new ArgumentException("别名（slug）不能为空");
            lock (Gate)
            {
                var map = ReadMap();
                var clean = new PrinterSlugEntry
                {
                    Slug = entry.Slug.Trim(),
                    Target = (entry.Target ?? "").Trim(),
                    Mode = string.IsNullOrWhiteSpace(entry.Mode) ? "auto" : entry.Mode.Trim().ToLowerInvariant(),
                    Note = entry.Note ?? "",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                map[clean.Slug] = clean;
                WriteMap(map);
                return clean;
            }
        }

        /// <summary>删除某别名；返回是否确有删除。</summary>
        public static bool Remove(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            lock (Gate)
            {
                var map = ReadMap();
                if (!map.ContainsKey(slug)) return false;
                map.Remove(slug);
                WriteMap(map);
                return true;
            }
        }

        // ------------------------------------------------ 持久化

        private static Dictionary<string, PrinterSlugEntry> ReadMap()
        {
            try
            {
                if (!File.Exists(Path)) return new Dictionary<string, PrinterSlugEntry>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(Path);
                var obj = JObject.Parse(json);
                var map = new Dictionary<string, PrinterSlugEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in obj.Properties())
                {
                    var e = prop.Value.ToObject<PrinterSlugEntry>();
                    if (e != null && !string.IsNullOrWhiteSpace(e.Slug))
                        map[prop.Name] = e;
                }
                return map;
            }
            catch
            {
                return new Dictionary<string, PrinterSlugEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void WriteMap(Dictionary<string, PrinterSlugEntry> map)
        {
            try
            {
                var json = new JObject();
                foreach (var kv in map)
                {
                    json[kv.Key] = JObject.FromObject(kv.Value,
                        new JsonSerializer { Formatting = Formatting.None });
                }
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, json.ToString(Formatting.Indented));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(tmp, Path);
            }
            catch (Exception ex)
            {
                DuoAssistant.Utils.Tools.Logger("写入打印机别名表失败: " + ex.Message);
            }
        }
    }
}

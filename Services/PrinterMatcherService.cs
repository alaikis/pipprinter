// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DuoAssistant.Services
{
    /// <summary>单台打印机的匹配结果（含理由与分数）。</summary>
    public class PrinterMatchResult
    {
        public string Printer { get; set; } = "";
        public string Driver { get; set; } = "";
        public int PaperWidthMm { get; set; }
        public string Reason { get; set; } = "";
        public int Score { get; set; }
        public bool IsDefault { get; set; }
        public bool IsPreferred { get; set; }
    }

    /// <summary>
    /// 三层打印机匹配引擎：
    ///  ① 文件类别 → 适配驱动类型（热敏/GDI/虚拟）过滤不匹配的驱动
    ///  ② 纸张宽度 → 与请求纸宽比较，过滤无法容纳的打印机
    ///  ③ 历史偏好 → 用户手动选过的同类文件打印机排最前
    /// 结果含完整候选列表，调用方可让前端弹出选择。
    /// 偏好持久化在 data/preferences.json，键为「文件类别」。
    /// </summary>
    public class PrinterMatcherService
    {
        private static readonly object Gate = new object();
        private static readonly string PrefsPath =
            Path.Combine(SettingsStore.Instance.DataDir, "preferences.json");

        // ------------------------------------------------ 历史偏好存储

        private class Prefs
        {
            public Dictionary<string, string> PreferredPrinterByCategory { get; set; }
                = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Prefs LoadPrefs()
        {
            try
            {
                if (!File.Exists(PrefsPath)) return new Prefs();
                var json = File.ReadAllText(PrefsPath);
                return JsonConvert.DeserializeObject<Prefs>(json) ?? new Prefs();
            }
            catch { return new Prefs(); }
        }

        private static void SavePrefs(Prefs prefs)
        {
            try
            {
                var dir = Path.GetDirectoryName(PrefsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = PrefsPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(prefs, Formatting.Indented));
                if (File.Exists(PrefsPath)) File.Delete(PrefsPath);
                File.Move(tmp, PrefsPath);
            }
            catch (Exception ex) { DuoAssistant.Utils.Tools.Logger("写入偏好文件失败: " + ex.Message); }
        }

        /// <summary>记录某文件类别的打印机选择（用户手动确认后调用）。</summary>
        public static void RecordPreference(string category, string printerName)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(printerName)) return;
            lock (Gate)
            {
                var prefs = LoadPrefs();
                prefs.PreferredPrinterByCategory[category] = printerName;
                SavePrefs(prefs);
                DuoAssistant.Utils.Tools.Logger("打印机偏好已记录 [" + category + "] → " + printerName);
            }
        }

        /// <summary>读取某文件类别的历史偏好；无记录返回 null。</summary>
        public static string GetPreference(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;
            var prefs = LoadPrefs();
            string val;
            return prefs.PreferredPrinterByCategory.TryGetValue(category, out val) ? val : null;
        }

        // ------------------------------------------------ 三层匹配入口

        /// <summary>
        /// 三层匹配：
        ///  - target      文件路径或 URL（用扩展名识别文件类别）
        ///  - paperWidthMm 期望纸宽（毫米），为 0 时跳过纸宽过滤
        ///  - prefer      可选，强制指定偏好打印机（优先级高于历史）
        /// 返回候选列表（已排序）+ 首选。
        /// </summary>
        public static List<PrinterMatchResult> Match(
            string target, int paperWidthMm, string prefer)
        {
            var cat = CategoryOfTarget(target);
            var installed = new PrinterService().GetInstalled();
            var installedNames = new HashSet<string>(
                installed.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            // L1：按类别过滤
            var l1 = FilterByCategory(installed, cat);

            // L2：按纸宽过滤
            var l2 = paperWidthMm > 0 ? FilterByPaperWidth(l1, paperWidthMm) : l1;

            // L3：按偏好排序
            string historyPref = GetPreference(cat);
            string effectivePref = !string.IsNullOrWhiteSpace(prefer) ? prefer : historyPref;
            var l3 = SortByPreference(l2, effectivePref, cat);

            // 补充：如果某打印机未在 installedNames 中（已被删除/驱动失效），标记为不可用
            var results = l3.Select(p =>
            {
                var info = installed.FirstOrDefault(i =>
                    string.Equals(i.Name, p, StringComparison.OrdinalIgnoreCase));
                return new PrinterMatchResult
                {
                    Printer = p,
                    Driver = info?.Driver ?? "未知",
                    PaperWidthMm = info != null ? MaxPaperWidthMm(info.PaperSizes) : 0,
                    IsDefault = info?.IsDefault ?? false,
                    IsPreferred = effectivePref != null &&
                                  string.Equals(p, effectivePref, StringComparison.OrdinalIgnoreCase),
                    Reason = BuildReason(info, p, effectivePref, cat),
                    Score = ComputeScore(info, p, effectivePref, cat, paperWidthMm)
                };
            }).Where(r => installedNames.Contains(r.Printer))
              .OrderByDescending(r => r.Score)
              .ThenBy(r => !r.IsPreferred)
              .ThenBy(r => !r.IsDefault)
              .ToList();

            return results;
        }

        /// <summary>便捷版：只返回首选打印机名；无匹配时返回空字符串。</summary>
        public static string MatchBest(string target, int paperWidthMm, string prefer)
        {
            var list = Match(target, paperWidthMm, prefer);
            return list.Count > 0 ? list[0].Printer : "";
        }

        // ------------------------------------------------ 各层实现

        private static string CategoryOfTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return "unknown";
            try
            {
                var t = target.Trim();
                if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("//", StringComparison.Ordinal))
                {
                    var abs = new Uri(t.StartsWith("//") ? "https:" + t : t).AbsolutePath;
                    return PrintFileTypes.CategoryOf(Path.GetExtension(abs));
                }
                return PrintFileTypes.CategoryOf(Path.GetExtension(t));
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// 文件类别 → 适配驱动类型（L1）。
        /// 热敏/标签打印机（PaperKind 含 "Receipt" / 驱动名含 "POS" / "ESC"）对 PDF/Office 无效；
        /// 常规 GDI 打印机可打所有类型；虚拟打印机（Microsoft Print to PDF / XPS Writer）不参与匹配。
        /// </summary>
        private static List<string> FilterByCategory(
            List<PrinterInfo> installed, string category)
        {
            // 虚拟打印机永远不参与实体匹配
            var nonVirtual = installed
                .Where(p => !IsVirtualPrinter(p))
                .Select(p => p.Name)
                .ToList();

            // 未知类别：不限制，返回全部非虚拟打印机
            if (category == "unknown") return nonVirtual;

            // 热敏/标签（escpos 类别）：只保留驱动名/名称含热敏特征的
            if (category == "escpos")
            {
                var thermal = installed
                    .Where(p => IsThermalPrinter(p))
                    .Select(p => p.Name)
                    .ToList();
                return thermal.Count > 0 ? thermal : nonVirtual; // 没有热敏就退回全部
            }

            // 其余类别（pdf/office/image/text/html/svg/xps/ofd）：GDI 打印均可，排除热敏
            var gdiCapable = installed
                .Where(p => !IsThermalPrinter(p))
                .Select(p => p.Name)
                .ToList();
            return gdiCapable.Count > 0 ? gdiCapable : nonVirtual;
        }

        private static List<string> FilterByPaperWidth(
            List<string> candidates, int paperWidthMm)
        {
            var result = new List<string>();
            foreach (var name in candidates)
            {
                // 无法读取纸宽的打印机（IsValid=false）直接保留，不假设
                // 能读到纸宽的最大值 < 请求纸宽 → 过滤
                var info = new PrinterService().GetInstalled()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (info == null || !info.IsValid) { result.Add(name); continue; }

                int maxW = MaxPaperWidthMm(info.PaperSizes);
                // maxW=0 表示未读到，不限制；否则要求 >= 请求纸宽
                if (maxW > 0 && maxW < paperWidthMm) continue;
                result.Add(name);
            }
            return result.Count > 0 ? result : candidates;
        }

        private static List<string> SortByPreference(
            List<string> candidates, string prefer, string category)
        {
            if (string.IsNullOrWhiteSpace(prefer) || candidates.Count <= 1) return candidates;
            var idx = candidates.FindIndex(x =>
                string.Equals(x, prefer, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return candidates;
            var reordered = new List<string>(candidates.Count);
            reordered.Add(candidates[idx]);
            reordered.AddRange(candidates.Where((x, i) => i != idx));
            return reordered;
        }

        // ------------------------------------------------ 辅助判断

        private static bool IsVirtualPrinter(PrinterInfo p)
        {
            var n = p.Name.ToUpperInvariant();
            var d = p.Driver.ToUpperInvariant();
            return n.Contains("PDF") && !n.Contains("PRINTER") || // Microsoft Print to PDF
                   n.Contains("XPS") && !n.Contains("PRINTER") ||
                   d.Contains("MSPDF") || d.Contains("XPSWRITER");
        }

        private static bool IsThermalPrinter(PrinterInfo p)
        {
            var n = p.Name.ToLowerInvariant();
            var d = p.Driver.ToLowerInvariant();
            return n.Contains("escpos") || n.Contains("pos") || n.Contains("receipt") ||
                   n.Contains("热敏") || n.Contains("小票") || n.Contains("标签") ||
                   d.Contains("esc") || d.Contains("pos") || d.Contains("receipt") ||
                   d.Contains("热敏");
        }

        /// <summary>从 PaperSizes 字符串解析最大纸张宽度（mm）；无法解析返回 0。</summary>
        private static int MaxPaperWidthMm(string paperSizes)
        {
            if (string.IsNullOrWhiteSpace(paperSizes)) return 0;
            int max = 0;
            // PaperSizes 形如 "A4 (210mm x 297mm), A3 (297mm x 420mm), Custom"
            foreach (var part in paperSizes.Split(','))
            {
                var p = part.Trim();
                // 尝试匹配 "Wmm x Hmm" 或 "WxH" 格式
                var m = System.Text.RegularExpressions.Regex.Match(
                    p, @"\(?(\d+)\s*mm?\s*x\s*(\d+)\s*mm?\)?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int w = int.Parse(m.Groups[1].Value);
                    int h = int.Parse(m.Groups[2].Value);
                    // 横向时宽度取 h
                    int width = Math.Max(w, h);
                    if (width > max) max = width;
                }
            }
            return max;
        }

        private static string BuildReason(
            PrinterInfo info, string name, string prefer, string category)
        {
            var parts = new List<string>();
            if (info != null && info.IsDefault) parts.Add("系统默认");
            if (prefer != null && string.Equals(name, prefer, StringComparison.OrdinalIgnoreCase))
                parts.Add("历史偏好");
            if (category == "escpos" && info != null && IsThermalPrinter(info))
                parts.Add("热敏适配");
            if (info == null || !info.IsValid) parts.Add("状态未知");
            return parts.Count > 0 ? string.Join("、", parts) : "匹配候选";
        }

        private static int ComputeScore(
            PrinterInfo info, string name, string prefer, string category, int paperWidthMm)
        {
            int score = 0;
            if (info == null || !info.IsValid) score += 0;      // 低分，不主动排前
            else score += 10;                                   // 驱动可用
            if (info != null && info.IsDefault) score += 20;     // 系统默认加权
            if (prefer != null && string.Equals(name, prefer, StringComparison.OrdinalIgnoreCase))
                score += 50;                                     // 历史偏好最高权重
            if (category == "escpos" && info != null && IsThermalPrinter(info))
                score += 15;                                      // 热敏适配
            if (paperWidthMm > 0 && info != null)
            {
                int maxW = MaxPaperWidthMm(info.PaperSizes);
                if (maxW > 0 && maxW >= paperWidthMm) score += 10; // 纸宽满足
            }
            return score;
        }
    }
}

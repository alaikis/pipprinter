// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>页数解析结果。Exact=false 表示估算值（不能用于精确计费，仅作兜底）。</summary>
    public class PageCountInfo
    {
        public int Pages { get; set; }
        public string Method { get; set; } = "";
        public bool Exact { get; set; }
        public override string ToString() => Pages + " (" + Method + (Exact ? ",exact" : ",est") + ")";
    }

    /// <summary>
    /// 统一页数解析：PDF 用 iTextSharp 精确读取（失败时字节扫描 page tree 兜底），
    /// 图片按帧数（GIF/TIFF 多帧）、文本按行数估算、XPS 走现有渲染组件计数。
    /// 用于配额预检、实际计费与审计流水的页数贯通，不再拿「份数」冒充页数。
    /// </summary>
    public static class PageCountService
    {
        /// <summary>解析本地文件的物理页数（未考虑页码范围与份数）。失败返回 0。</summary>
        public static PageCountInfo Count(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new PageCountInfo { Pages = 0, Method = "missing" };
            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".pdf")
                {
                    // 1) iTextSharp 精确解析（NU1701 兼容包，运行时不可用时自动兜底）
                    try
                    {
                        using (var reader = new iTextSharp.text.pdf.PdfReader(path))
                        {
                            int n = reader.NumberOfPages;
                            if (n > 0) return new PageCountInfo { Pages = n, Method = "pdf-reader", Exact = true };
                        }
                    }
                    catch (Exception ex) { Tools.Logger("iTextSharp 页数解析回退字节扫描: " + ex.Message); }
                    // 2) 字节扫描 /Count N（page tree 根节点），再退 /Type/Page 计数
                    int scanned = ScanPdfPages(path);
                    if (scanned > 0) return new PageCountInfo { Pages = scanned, Method = "pdf-scan", Exact = true };
                    return new PageCountInfo { Pages = 0, Method = "pdf-unknown" };
                }

                if (IsImage(ext))
                {
                    using var img = Image.FromFile(path);
                    int frames = 1;
                    try
                    {
                        var dims = img.FrameDimensionsList;
                        if (dims != null && dims.Length > 0)
                            frames = Math.Max(1, img.GetFrameCount(new FrameDimension(dims[0])));
                    }
                    catch { frames = 1; }
                    return new PageCountInfo { Pages = frames, Method = "image-frames", Exact = true };
                }

                if (IsText(ext))
                {
                    // 与 NativePrintService.PrintTextCore 同口径：约 45 行/页（含自动换行按 60 字符估算）
                    var text = File.ReadAllText(path);
                    int lines = 0;
                    using (var sr = new StringReader(text))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                            lines += Math.Max(1, (int)Math.Ceiling(line.Length / 60.0));
                    }
                    int pages = Math.Max(1, (int)Math.Ceiling(lines / 45.0));
                    return new PageCountInfo { Pages = pages, Method = "text-estimate", Exact = false };
                }

                if (ext == ".xps" || ext == ".oxps")
                {
                    try
                    {
                        var pages = new XpsWriterService().RenderPages(path);
                        int n = pages.Count;
                        foreach (var p in pages)
                            try { if (File.Exists(p)) File.Delete(p); } catch { }
                        if (n > 0) return new PageCountInfo { Pages = n, Method = "xps-render", Exact = true };
                    }
                    catch (Exception ex) { Tools.Logger("XPS 页数解析失败: " + ex.Message); }
                    return new PageCountInfo { Pages = 0, Method = "xps-unknown" };
                }

                // SVG / 其它固定单页类型
                if (ext == ".svg")
                    return new PageCountInfo { Pages = 1, Method = "svg-single", Exact = true };

                return new PageCountInfo { Pages = 0, Method = "unknown-type" };
            }
            catch (Exception ex)
            {
                Tools.Logger("页数解析失败(" + ext + "): " + ex.Message);
                return new PageCountInfo { Pages = 0, Method = "error" };
            }
        }

        /// <summary>
        /// 解析页码范围字符串，返回实际选中页数。支持 "1-" / "1-3" / "2,4,6" / 混合。
        /// total<=0 或 spec 解析失败时返回 total（不缩小范围）。
        /// </summary>
        public static int SelectedCount(string spec, int total)
        {
            if (total <= 0) return total;
            spec = (spec ?? "").Trim();
            if (spec.Length == 0 || spec == "1-" || spec == "all") return total;
            int sum = 0;
            bool any = false;
            foreach (var raw in spec.Split(','))
            {
                var seg = raw.Trim();
                if (seg.Length == 0) continue;
                int a, b;
                if (seg.Contains("-"))
                {
                    var parts = seg.Split('-');
                    if (!int.TryParse(parts[0].Trim(), out a)) continue;
                    if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out b) || b <= 0) b = total;
                }
                else
                {
                    if (!int.TryParse(seg, out a)) continue;
                    b = a;
                }
                a = Math.Max(1, Math.Min(total, a));
                b = Math.Max(a, Math.Min(total, b));
                sum += b - a + 1;
                any = true;
            }
            return any ? Math.Max(1, Math.Min(total, sum)) : total;
        }

        /// <summary>按本地文件 + 页码范围 + 份数算出本次任务的计费页数。</summary>
        public static int Billable(string path, string pagesSpec, int copies)
        {
            var info = Count(path);
            if (info.Pages <= 0) return 0;
            return SelectedCount(pagesSpec, info.Pages) * Math.Max(1, copies);
        }

        /// <summary>
        /// 入队预检用：优先解析本地文件；URL/内联内容无法预知时返回调用方声明值（billablePages），
        /// 仍无法判断时返回 0 表示「不限」（打印后按实际页数补记账，绝不拒绝合法任务）。
        /// </summary>
        public static int EstimateForCheck(PrintJob job)
        {
            if (job == null) return 0;
            if (job.BillablePages > 0) return job.BillablePages;
            if (job.Source == "file" && !string.IsNullOrWhiteSpace(job.Target))
            {
                var n = Billable(job.Target, job.Pages, job.Copies);
                if (n > 0) return n;
            }
            return 0; // 未知页数：放行，打印后按实际值记账
        }

        // ------------------------------------------------ 内部

        private static bool IsImage(string ext) =>
            ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" ||
            ext == ".gif" || ext == ".tif" || ext == ".tiff";

        private static bool IsText(string ext) =>
            ext == ".txt" || ext == ".csv" || ext == ".log" || ext == ".md";

        /// <summary>扫描 PDF 字节流里的 /Count N（取最大值，根 page tree 通常是最大者）。</summary>
        private static int ScanPdfPages(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var text = Encoding.Latin1.GetString(bytes);
                int best = 0;
                int idx = 0;
                while ((idx = text.IndexOf("/Count", idx, StringComparison.Ordinal)) >= 0)
                {
                    int p = idx + 6;
                    while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                    int start = p;
                    while (p < text.Length && char.IsDigit(text[p])) p++;
                    if (p > start && int.TryParse(text.Substring(start, p - start), out var n) && n > best && n < 1000000)
                        best = n;
                    idx = p;
                }
                if (best > 0) return best;
                // 退化：统计 /Type/Page（排除 /Pages）出现次数
                int count = 0;
                idx = 0;
                while ((idx = text.IndexOf("/Type", idx, StringComparison.Ordinal)) >= 0)
                {
                    int p = idx + 5;
                    while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                    if (p + 5 <= text.Length && text.Substring(p, 5) == "/Page")
                    {
                        int after = p + 5;
                        if (after >= text.Length || !char.IsLetter(text[after])) count++;
                    }
                    idx = p;
                }
                return count;
            }
            catch { return 0; }
        }
    }
}

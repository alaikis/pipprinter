// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// GDI+ 原生打印引擎（System.Drawing.Printing.PrintDocument）。
    /// 直接渲染图片与文本，不依赖 Office / 看图软件 / 系统 Shell 关联：
    ///   · PrintImage  图片（PNG/JPG/BMP/GIF/TIFF，含多帧 TIFF/GIF 逐页）
    ///   · PrintText   文本（TXT/CSV/LOG/MD，自动换行、分页、页眉页脚、页码）
    ///   · 虚拟输出    Microsoft Print to PDF / Microsoft XPS Document Writer → 输出文件
    /// 支持份数、纸张（A0–A5/letter/legal/tabloid/statement/自定义毫米）、方向、缩放、边距。
    /// </summary>
    public class NativePrintService
    {
        public const string PdfVirtualPrinter = "Microsoft Print to PDF";
        public const string XpsVirtualPrinter = "Microsoft XPS Document Writer";

        // ---------------------------------------------------------------- 图片

        public PrintOutcome PrintImage(string file, PrintJob job, string printer)
        {
            return PrintImageCore(file, job, printer, null);
        }

        /// <summary>图片 → 虚拟打印机输出文件（PDF/XPS）。</summary>
        public PrintOutcome PrintImageToVirtual(string file, PrintJob job, string virtualPrinter, string outPath)
        {
            return PrintImageCore(file, job, virtualPrinter, outPath);
        }

        /// <summary>
        /// 多页图片打印（每张图片一页，同一个 PrintDocument，保证一份打印作业）。
        /// 用于 XPS 文档渲染结果等分页位图序列。
        /// </summary>
        public PrintOutcome PrintImages(System.Collections.Generic.IList<string> files, PrintJob job, string printer)
        {
            return PrintImagesCore(files, job, printer, null);
        }

        public PrintOutcome PrintImagesToVirtual(System.Collections.Generic.IList<string> files, PrintJob job,
            string virtualPrinter, string outPath)
        {
            return PrintImagesCore(files, job, virtualPrinter, outPath);
        }

        private PrintOutcome PrintImagesCore(System.Collections.Generic.IList<string> files, PrintJob job,
            string printer, string outPath)
        {
            if (files == null || files.Count == 0) return Fail("没有可打印的页面");
            foreach (var f in files)
                if (!File.Exists(f)) return Fail("文件不存在: " + f);

            var images = new List<Image>();
            try
            {
                for (int i = 0; i < files.Count; i++) images.Add(Image.FromFile(files[i]));

                var doc = new PrintDocument();
                var configured = Configure(doc, job, printer, outPath);
                if (!configured.Ok) return configured;

                int page = 0;
                doc.PrintPage += (s, e) =>
                {
                    try
                    {
                        var img = images[page];
                        var g = e.Graphics;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        var bounds = e.MarginBounds;
                        var scale = ComputeScale(img, bounds, job.Fit);
                        var w = img.Width * scale;
                        var h = img.Height * scale;
                        var x = bounds.X + (bounds.Width - w) / 2f;
                        var y = bounds.Y + (bounds.Height - h) / 2f;
                        g.DrawImage(img, x, y, w, h);

                        page++;
                        e.HasMorePages = page < images.Count;
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger("PrintPage 渲染异常: " + ex);
                        e.HasMorePages = false;
                    }
                };

                return Print(doc, "GDI+Images", printer, outPath);
            }
            catch (Exception ex)
            {
                Tools.Logger("多页图片打印失败: " + ex);
                return Fail("打印失败: " + ex.Message);
            }
            finally
            {
                foreach (var img in images)
                { try { img.Dispose(); } catch { /* 忽略 */ } }
            }
        }

        private PrintOutcome PrintImageCore(string file, PrintJob job, string printer, string outPath)
        {
            if (!File.Exists(file)) return Fail("文件不存在: " + file);

            using (var img = Image.FromFile(file))
            {
                // .NET 10 参考程序集不再暴露 Image.FrameDimensions 属性，
                // 改为依次探测 Page（多页 TIFF）与 Time（多帧 GIF）维度。
                FrameDimension dimension = null;
                int frameCount = 1;
                try
                {
                    int pages = img.GetFrameCount(FrameDimension.Page);
                    if (pages > 1) { dimension = FrameDimension.Page; frameCount = pages; }
                }
                catch { /* 非 Page 维度（如 GIF）会抛异常，忽略 */ }
                if (dimension == null)
                {
                    try
                    {
                        int frames = img.GetFrameCount(FrameDimension.Time);
                        if (frames > 1) { dimension = FrameDimension.Time; frameCount = frames; }
                    }
                    catch { /* 单帧图片忽略 */ }
                }
                if (frameCount < 1) frameCount = 1;

                var doc = new PrintDocument();
                var configured = Configure(doc, job, printer, outPath);
                if (!configured.Ok) return configured;

                var currentPage = 0;
                doc.PrintPage += (s, e) =>
                {
                    try
                    {
                        if (currentPage > 0 && dimension != null)
                        {
                            try { img.SelectActiveFrame(dimension, currentPage); }
                            catch { /* 单帧图片忽略 */ }
                        }

                        var g = e.Graphics;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        var bounds = e.MarginBounds;
                        var scale = ComputeScale(img, bounds, job.Fit);
                        var w = img.Width * scale;
                        var h = img.Height * scale;
                        var x = bounds.X + (bounds.Width - w) / 2f;
                        var y = bounds.Y + (bounds.Height - h) / 2f;
                        g.DrawImage(img, x, y, w, h);

                        currentPage++;
                        e.HasMorePages = currentPage < frameCount;
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger("PrintPage 渲染异常: " + ex);
                        e.HasMorePages = false;
                    }
                };

                return Print(doc, "GDI+Image", printer, outPath);
            }
        }

        // ---------------------------------------------------------------- 文本

        public PrintOutcome PrintText(string file, PrintJob job, string printer)
        {
            return PrintTextCore(file, job, printer, null);
        }

        public PrintOutcome PrintTextToVirtual(string file, PrintJob job, string virtualPrinter, string outPath)
        {
            return PrintTextCore(file, job, virtualPrinter, outPath);
        }

        private PrintOutcome PrintTextCore(string file, PrintJob job, string printer, string outPath)
        {
            if (!File.Exists(file)) return Fail("文件不存在: " + file);

            string content;
            try { content = File.ReadAllText(file); }
            catch (Exception ex) { return Fail("读取文本失败: " + ex.Message); }

            var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var fileName = Path.GetFileName(file);

            var doc = new PrintDocument();
            var configured = Configure(doc, job, printer, outPath);
            if (!configured.Ok) return configured;

            var font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
            var headerFont = new Font("Segoe UI", 8f, FontStyle.Italic, GraphicsUnit.Point);
            var pageIndex = 0;

            doc.PrintPage += (s, e) =>
            {
                try
                {
                    var g = e.Graphics;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    var bounds = e.MarginBounds;

                    float headerH = 0f;
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        headerH = headerFont.GetHeight(g) + 6f;
                        g.DrawString(fileName, headerFont, Brushes.Gray, bounds.X, bounds.Y);
                        g.DrawLine(Pens.LightGray, bounds.X, bounds.Y + headerH - 2,
                            bounds.Right, bounds.Y + headerH - 2);
                    }

                    var textTop = bounds.Y + headerH + 4f;
                    var textBottom = bounds.Bottom - (headerFont.GetHeight(g) + 6f);
                    var lineHeight = font.GetHeight(g);
                    var usableHeight = textBottom - textTop;
                    var linesPerPage = Math.Max(1, (int)(usableHeight / lineHeight));

                    // 按页宽自动换行
                    var wrapped = WrapLines(rawLines, font, g, bounds.Width);
                    var start = pageIndex * linesPerPage;
                    var count = Math.Min(linesPerPage, wrapped.Count - start);
                    for (int i = 0; i < count; i++)
                    {
                        g.DrawString(wrapped[start + i], font, Brushes.Black,
                            bounds.X, textTop + i * lineHeight);
                    }

                    // 页脚页码
                    var totalPages = Math.Max(1, (int)Math.Ceiling(wrapped.Count / (double)linesPerPage));
                    var footer = (pageIndex + 1) + " / " + totalPages;
                    var fw = g.MeasureString(footer, headerFont).Width;
                    g.DrawString(footer, headerFont, Brushes.Gray,
                        bounds.Right - fw, bounds.Bottom - headerFont.GetHeight(g));

                    pageIndex++;
                    e.HasMorePages = start + count < wrapped.Count;
                }
                catch (Exception ex)
                {
                    Tools.Logger("PrintPage 文本渲染异常: " + ex);
                    e.HasMorePages = false;
                }
            };

            return Print(doc, "GDI+Text", printer, outPath);
        }

        private static List<string> WrapLines(string[] lines, Font font, Graphics g, float maxWidth)
        {
            var result = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) { result.Add(""); continue; }
                var remaining = line;
                while (g.MeasureString(remaining, font).Width > maxWidth && remaining.Length > 1)
                {
                    int cut = remaining.Length;
                    while (cut > 1 && g.MeasureString(remaining.Substring(0, cut), font).Width > maxWidth)
                        cut--;
                    result.Add(remaining.Substring(0, cut));
                    remaining = remaining.Substring(cut);
                }
                result.Add(remaining);
            }
            return result;
        }

        // ---------------------------------------------------------------- 公共配置

        /// <summary>配置打印机/份数/纸张/方向/边距；outPath 非空时走虚拟打印到文件。</summary>
        private static PrintOutcome Configure(PrintDocument doc, PrintJob job, string printer, string outPath)
        {
            try
            {
                // 虚拟打印（输出到文件）不允许静默回退：目标虚拟打印机不存在时
                // 回退到默认打印机会生成错误格式的文件（如把 XPS 请求变成 PDF）。
                bool virtualOutput = !string.IsNullOrEmpty(outPath);
                var name = ResolvePrinterName(printer, !virtualOutput);
                if (string.IsNullOrEmpty(name))
                    return Fail("没有可用的打印机");
                if (virtualOutput && !MatchesPrinter(name, printer))
                    return Fail("未安装虚拟打印机「" + printer + "」。已安装的打印机："
                        + string.Join(" / ", InstalledPrinterNames())
                        + "。可通过「设置 → 应用 → 可选功能」安装对应虚拟打印机后重试。");

                // 后台打印到「输出到文件」型虚拟打印机（如 Microsoft Print to PDF）时，
                // 驱动会弹出保存对话框并阻塞调用线程，导致整个打印队列卡死。
                // 这里直接拒绝并给出可执行的替代方案。
                if (!virtualOutput && IsVirtualFilePrinter(name))
                    return Fail("目标打印机「" + name + "」是虚拟文档打印机，直接打印会弹出保存对话框并阻塞打印队列。"
                        + "请指定实体打印机，或使用 PrintToPdf / PrintToXps 直接输出文件。");

                doc.PrinterSettings.PrinterName = name;

                if (job.Copies > 1)
                {
                    try { doc.PrinterSettings.Copies = (short)Math.Min(999, job.Copies); }
                    catch { /* 部分打印机不支持份数 */ }
                }

                if (!string.IsNullOrWhiteSpace(job.PaperKind))
                    ApplyPaper(doc, job.PaperKind);

                doc.DefaultPageSettings.Landscape =
                    string.Equals(job.Scale, "landscape", StringComparison.OrdinalIgnoreCase);

                // 0.5 英寸边距（百分之一英寸单位）
                doc.DefaultPageSettings.Margins = new Margins(50, 50, 50, 50);

                if (!string.IsNullOrEmpty(outPath))
                {
                    doc.PrinterSettings.PrintToFile = true;
                    doc.PrinterSettings.PrintFileName = outPath;
                }
                return new PrintOutcome { Ok = true };
            }
            catch (Exception ex)
            {
                return Fail("打印机配置失败: " + ex.Message);
            }
        }

        private static PrintOutcome Print(PrintDocument doc, string engine, string printer, string outPath)
        {
            try
            {
                doc.Print();
                if (!string.IsNullOrEmpty(outPath))
                {
                    if (!File.Exists(outPath))
                        return Fail("虚拟打印未生成文件: " + outPath);
                    return new PrintOutcome
                    {
                        Ok = true, Engine = engine,
                        Message = "已输出到 " + outPath,
                        Detail = "打印机: " + doc.PrinterSettings.PrinterName
                    };
                }
                return new PrintOutcome
                {
                    Ok = true, Engine = engine,
                    Message = "已提交到打印机 " + doc.PrinterSettings.PrinterName,
                    Detail = "GDI+ 原生渲染，不依赖 Office / 看图软件"
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("GDI+ 打印失败: " + ex);
                return Fail("打印失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 解析打印机名：精确 → 大小写不敏感 → 部分匹配 →（可选）系统默认。
        /// fallbackToDefault=false 时只做匹配，找不到返回空串（供虚拟打印等严格场景使用）。
        /// </summary>
        public static string ResolvePrinterName(string printer, bool fallbackToDefault = true)
        {
            try
            {
                var installed = InstalledPrinterNames();
                if (string.IsNullOrWhiteSpace(printer))
                    return fallbackToDefault ? DefaultPrinterName() : "";

                var exact = installed.FirstOrDefault(
                    n => string.Equals(n, printer, StringComparison.Ordinal));
                if (exact != null) return exact;

                var ignoreCase = installed.FirstOrDefault(
                    n => n.Equals(printer, StringComparison.OrdinalIgnoreCase));
                if (ignoreCase != null) return ignoreCase;

                var partial = installed.FirstOrDefault(
                    n => n.IndexOf(printer, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partial != null) return partial;

                return fallbackToDefault ? DefaultPrinterName() : "";
            }
            catch (Exception ex)
            {
                Tools.Logger("解析打印机名失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 「输出到文件」型虚拟打印机的名称特征。
        /// 这类驱动在 PrintToFile=false 时会弹模态保存对话框，阻塞后台打印线程。
        /// </summary>
        private static readonly string[] VirtualFilePrinterHints = new[]
        {
            "print to pdf", "xps document writer", "pdf writer", "pdf printer",
            "wps pdf", "导出为wps", "foxit pdf", "adobe pdf", "save as pdf",
            "microsoft print to", "virtual printer", "虚拟打印机", "pdf 打印机", "xps 文档"
        };

        /// <summary>判断是否为「输出到文件」型虚拟打印机。</summary>
        public static bool IsVirtualFilePrinter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.ToLowerInvariant();
            foreach (var hint in VirtualFilePrinterHints)
                if (n.Contains(hint)) return true;
            return false;
        }

        /// <summary>判断解析结果是否就是调用方请求的打印机（精确 / 忽略大小写 / 部分匹配）。</summary>
        private static bool MatchesPrinter(string resolved, string requested)
        {
            if (string.IsNullOrEmpty(resolved) || string.IsNullOrEmpty(requested)) return false;
            if (string.Equals(resolved, requested, StringComparison.OrdinalIgnoreCase)) return true;
            return resolved.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf(resolved, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>已安装的打印机名单（失败返回空列表）。</summary>
        public static List<string> InstalledPrinterNames()
        {
            try { return PrinterSettings.InstalledPrinters.Cast<string>().ToList(); }
            catch (Exception ex)
            {
                Tools.Logger("枚举打印机失败: " + ex.Message);
                return new List<string>();
            }
        }

        /// <summary>系统默认打印机名（失败返回空串）。</summary>
        public static string DefaultPrinterName()
        {
            try { return new PrinterSettings().PrinterName ?? ""; }
            catch (Exception ex)
            {
                Tools.Logger("读取默认打印机失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>纸张：A0–A5 / letter / legal / tabloid / statement / 自定义毫米（如 841mm x 1189mm）。</summary>
        public static void ApplyPaper(PrintDocument doc, string paper)
        {
            var p = (paper ?? "").Trim();
            if (p.Length == 0) return;

            int w, h;
            if (TryParseMm(p, out w, out h))
            {
                doc.DefaultPageSettings.PaperSize = new PaperSize("Custom", w, h);
                return;
            }

            switch (p.ToUpperInvariant())
            {
                case "A0": doc.DefaultPageSettings.PaperSize = new PaperSize("A0", 3311, 4681); break;
                case "A1": doc.DefaultPageSettings.PaperSize = new PaperSize("A1", 2339, 3311); break;
                case "A2": doc.DefaultPageSettings.PaperSize = new PaperSize("A2", 1654, 2339); break;
                case "A3": doc.DefaultPageSettings.PaperSize = new PaperSize("A3", 1169, 1654); break;
                case "A4": doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169); break;
                case "A5": doc.DefaultPageSettings.PaperSize = new PaperSize("A5", 583, 827); break;
                case "LETTER": doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100); break;
                case "LEGAL": doc.DefaultPageSettings.PaperSize = new PaperSize("Legal", 850, 1400); break;
                case "TABLOID": doc.DefaultPageSettings.PaperSize = new PaperSize("Tabloid", 1100, 1700); break;
                case "STATEMENT": doc.DefaultPageSettings.PaperSize = new PaperSize("Statement", 550, 850); break;
                default:
                    Tools.Logger("未知纸张规格，使用打印机默认: " + p);
                    break;
            }
        }

        /// <summary>解析 "841mm x 1189mm" 形式的自定义纸张（返回百分之一英寸）。</summary>
        private static bool TryParseMm(string text, out int width, out int height)
        {
            width = height = 0;
            var parts = text.ToLowerInvariant().Split('x');
            if (parts.Length != 2) return false;
            if (!TryParseMmValue(parts[0], out width)) return false;
            if (!TryParseMmValue(parts[1], out height)) return false;
            return true;
        }

        private static bool TryParseMmValue(string s, out int hundredthsInch)
        {
            hundredthsInch = 0;
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("mm")) s = s.Substring(0, s.Length - 2).Trim();
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mm))
                return false;
            hundredthsInch = (int)Math.Round(mm * 3.937007874);
            return hundredthsInch > 0;
        }

        private static float ComputeScale(Image img, Rectangle bounds, string fit)
        {
            var sx = bounds.Width / (float)img.Width;
            var sy = bounds.Height / (float)img.Height;
            var s = Math.Min(sx, sy);
            switch ((fit ?? "fit").ToLowerInvariant())
            {
                case "noscale": return 1f;
                case "shrink": return Math.Min(s, 1f);
                case "compat": return 1f;
                default: return s;
            }
        }

        private static PrintOutcome Fail(string message) =>
            new PrintOutcome { Ok = false, Message = message };
    }
}

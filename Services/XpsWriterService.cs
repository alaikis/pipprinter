// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You may use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of this software at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 内置 XPS 写入器（基于 WPF 的 System.Windows.Xps.Packaging）。
    ///
    /// 为什么不用「Microsoft XPS Document Writer」虚拟打印机：
    ///   该打印机是 Windows 的可选功能，很多机器（含 Win11 精简镜像）默认未安装，
    ///   此时打印到 XPS 会静默失败或生成错误格式。内置写入器不依赖任何可选功能，
    ///   在任意 Windows 上都能直接产出标准 OPC/XPS 文档。
    ///
    /// 线程模型：WPF 的 DependencyObject / FixedPage 需要 STA 线程，
    /// 而打印队列工作线程是 Task.Run 的 MTA 线程，故统一在独立 STA 线程中执行。
    /// </summary>
    public sealed class XpsWriterService
    {
        private const double Dpi = 96.0;                 // WPF 逻辑单位 = 1/96 英寸
        // 边距由 PrintJob.Margin（1/100 英寸，默认 50 = 0.5 英寸）统一提供
        private static double MarginUnits(PrintJob job)
        {
            var m = job == null ? 50 : Math.Max(0, Math.Min(500, job.Margin));
            return m / 100.0 * Dpi;
        }
        private const int TimeoutSeconds = 120;

        // ---------------------------------------------------------------- 图片

        public PrintOutcome WriteImage(string file, PrintJob job, string outPath)
        {
            return RunOnSta(() => WriteImageCore(file, job, outPath));
        }

        private PrintOutcome WriteImageCore(string file, PrintJob job, string outPath)
        {
            if (!File.Exists(file)) return Fail("文件不存在: " + file);

            BitmapImage bitmap;
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Path.GetFullPath(file));
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
            }
            catch (Exception ex)
            {
                return Fail("读取图片失败: " + ex.Message);
            }

            var page = PageSize(job);
            var landscape = IsLandscape(job);
            double pageW = landscape ? page.Height : page.Width;
            double pageH = landscape ? page.Width : page.Height;
            double margin = MarginUnits(job);
            var bounds = new Rect(margin, margin, pageW - margin * 2, pageH - margin * 2);

            double scale = ComputeScale(bitmap.PixelWidth, bitmap.PixelHeight, bounds.Width, bounds.Height, job.Fit);
            double w = bitmap.PixelWidth * scale;
            double h = bitmap.PixelHeight * scale;
            double x = bounds.X + (bounds.Width - w) / 2.0;
            double y = bounds.Y + (bounds.Height - h) / 2.0;

            try
            {
                var fd = new FixedDocument();
                var fixedPage = new FixedPage();
                fixedPage.Width = pageW;
                fixedPage.Height = pageH;

                var img = new Image();
                img.Source = bitmap;
                img.Width = w;
                img.Height = h;
                FixedPage.SetLeft(img, x);
                FixedPage.SetTop(img, y);
                fixedPage.Children.Add(img);

                fd.Pages.Add(BuildPageContent(fixedPage));
                return Save(fd, outPath, "XPS+Image");
            }
            catch (Exception ex)
            {
                Tools.Logger("XPS 图片写入失败: " + ex);
                return Fail("XPS 写入失败: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 文本

        public PrintOutcome WriteText(string file, PrintJob job, string outPath)
        {
            return RunOnSta(() => WriteTextCore(file, job, outPath));
        }

        private PrintOutcome WriteTextCore(string file, PrintJob job, string outPath)
        {
            if (!File.Exists(file)) return Fail("文件不存在: " + file);

            string content;
            try { content = File.ReadAllText(file); }
            catch (Exception ex) { return Fail("读取文本失败: " + ex.Message); }

            var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var fileName = Path.GetFileName(file);

            var page = PageSize(job);
            var landscape = IsLandscape(job);
            double pageW = landscape ? page.Height : page.Width;
            double pageH = landscape ? page.Width : page.Height;
            double margin = MarginUnits(job);
            double textWidth = pageW - margin * 2;

            var bodyFont = new FontFamily("Consolas");
            var headerFont = new FontFamily("Segoe UI");
            double bodyPt = 10.0;
            double headerPt = 8.0;

            try
            {
                // 按页宽自动换行
                var wrapped = WrapLines(rawLines, bodyFont, bodyPt, textWidth);
                double lineHeight = MeasureHeight(bodyFont, bodyPt);
                double headerH = MeasureHeight(headerFont, headerPt) + 6.0;
                double textTop = margin + headerH + 4.0;
                double textBottom = pageH - margin - (MeasureHeight(headerFont, headerPt) + 6.0);
                int linesPerPage = Math.Max(1, (int)((textBottom - textTop) / lineHeight));

                int totalPages = Math.Max(1, (int)Math.Ceiling(wrapped.Count / (double)linesPerPage));
                var fd = new FixedDocument();

                for (int p = 0; p < totalPages; p++)
                {
                    var fixedPage = new FixedPage();
                    fixedPage.Width = pageW;
                    fixedPage.Height = pageH;

                    // 页眉：文件名 + 分隔线
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        var header = new TextBlock
                        {
                            Text = fileName,
                            FontFamily = headerFont,
                            FontSize = headerPt,
                            FontStyle = FontStyles.Italic,
                            Foreground = Brushes.Gray
                        };
                        FixedPage.SetLeft(header, margin);
                        FixedPage.SetTop(header, margin);
                        fixedPage.Children.Add(header);

                        var line = new System.Windows.Shapes.Line
                        {
                            X1 = margin, X2 = pageW - margin,
                            Y1 = margin + headerH - 2, Y2 = margin + headerH - 2,
                            Stroke = Brushes.LightGray,
                            StrokeThickness = 1
                        };
                        fixedPage.Children.Add(line);
                    }

                    int start = p * linesPerPage;
                    int count = Math.Min(linesPerPage, wrapped.Count - start);
                    for (int i = 0; i < count; i++)
                    {
                        var tb = new TextBlock
                        {
                            Text = wrapped[start + i],
                            FontFamily = bodyFont,
                            FontSize = bodyPt,
                            Foreground = Brushes.Black
                        };
                        FixedPage.SetLeft(tb, margin);
                        FixedPage.SetTop(tb, textTop + i * lineHeight);
                        fixedPage.Children.Add(tb);
                    }

                    // 页脚页码
                    var footer = new TextBlock
                    {
                        Text = (p + 1) + " / " + totalPages,
                        FontFamily = headerFont,
                        FontSize = headerPt,
                        Foreground = Brushes.Gray
                    };
                    double fw = MeasureWidth((p + 1) + " / " + totalPages, headerFont, headerPt);
                    FixedPage.SetLeft(footer, pageW - margin - fw);
                    FixedPage.SetTop(footer, pageH - margin - MeasureHeight(headerFont, headerPt));
                    fixedPage.Children.Add(footer);

                    fd.Pages.Add(BuildPageContent(fixedPage));
                }

                return Save(fd, outPath, "XPS+Text");
            }
            catch (Exception ex)
            {
                Tools.Logger("XPS 文本写入失败: " + ex);
                return Fail("XPS 写入失败: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 读取 XPS

        /// <summary>
        /// 把 XPS 文档每页渲染为临时 PNG（300 DPI，长边封顶 6000px）。
        /// 失败返回空列表。调用方负责在打印完成后删除这些临时文件。
        /// </summary>
        public List<string> RenderPages(string file)
        {
            var pages = new List<string>();
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { pages = RenderPagesCore(file); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "XpsRenderer";
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                Tools.Logger("XPS 渲染超时: " + file);
                return new List<string>();
            }
            if (error != null)
            {
                Tools.Logger("XPS 渲染失败: " + error.Message);
                return new List<string>();
            }
            return pages;
        }

        private static List<string> RenderPagesCore(string file)
        {
            var result = new List<string>();
            using (var doc = new XpsDocument(Path.GetFullPath(file), FileAccess.Read))
            {
                var fds = doc.GetFixedDocumentSequence();
                if (fds == null) return result;
                var paginator = ((System.Windows.Documents.IDocumentPaginatorSource)fds).DocumentPaginator;
                int count = paginator.PageCount;

                for (int i = 0; i < count; i++)
                {
                    var page = paginator.GetPage(i);
                    var size = page.Size;
                    if (size.Width <= 0 || size.Height <= 0) continue;

                    int w = (int)Math.Max(1, size.Width * 300.0 / Dpi);
                    int h = (int)Math.Max(1, size.Height * 300.0 / Dpi);
                    if (Math.Max(w, h) > 6000)
                    {
                        double k = 6000.0 / Math.Max(w, h);
                        w = (int)(w * k); h = (int)(h * k);
                    }

                    var rtb = new RenderTargetBitmap(w, h, 300, 300, PixelFormats.Pbgra32);
                    rtb.Render(page.Visual);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    var path = Path.Combine(SettingsStore.Instance.TempDir,
                        "xpspage_" + Guid.NewGuid().ToString("N") + ".png");
                    using (var fs = new FileStream(path, FileMode.Create))
                    {
                        encoder.Save(fs);
                    }
                    if (File.Exists(path)) result.Add(path);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- 辅助

        private static PageContent BuildPageContent(FixedPage page)
        {
            var pc = new PageContent();
            ((System.Windows.Markup.IAddChild)pc).AddChild(page);
            return pc;
        }

        private static PrintOutcome Save(FixedDocument fd, string outPath, string engine)
        {
            var full = Path.GetFullPath(outPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(full)) File.Delete(full);

            using (var doc = new XpsDocument(full, FileAccess.Write))
            {
                var writer = XpsDocument.CreateXpsDocumentWriter(doc);
                writer.Write(fd);
            }
            return new PrintOutcome
            {
                Ok = true,
                Engine = engine,
                Message = "已输出到 " + full,
                Detail = "内置 XPS 写入器（不依赖 Microsoft XPS Document Writer 可选功能）"
            };
        }

        /// <summary>在独立 STA 线程上执行 WPF 写入（打印队列工作线程是 MTA，不能直接创建 WPF 对象）。</summary>
        private static PrintOutcome RunOnSta(Func<PrintOutcome> action)
        {
            PrintOutcome result = null;
            var thread = new Thread(() =>
            {
                try { result = action(); }
                catch (Exception ex)
                {
                    Tools.Logger("XPS 写入线程异常: " + ex);
                    result = Fail("XPS 写入异常: " + ex.Message);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "XpsWriter";
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(TimeoutSeconds)))
                return Fail("XPS 写入超时（" + TimeoutSeconds + " 秒）");
            return result ?? Fail("XPS 写入未返回结果");
        }

        private static List<string> WrapLines(string[] lines, FontFamily font, double pt, double maxWidth)
        {
            var result = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) { result.Add(""); continue; }
                var remaining = line;
                while (MeasureWidth(remaining, font, pt) > maxWidth && remaining.Length > 1)
                {
                    int cut = remaining.Length;
                    while (cut > 1 && MeasureWidth(remaining.Substring(0, cut), font, pt) > maxWidth)
                        cut--;
                    result.Add(remaining.Substring(0, cut));
                    remaining = remaining.Substring(cut);
                }
                result.Add(remaining);
            }
            return result;
        }

        private static Typeface TypefaceOf(FontFamily family)
        {
            try
            {
                if (family == null) return null;
                return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            }
            catch { return null; }
        }

        private static double MeasureWidth(string text, FontFamily family, double pt)
        {
            try
            {
                var ft = new FormattedText(text ?? "", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, TypefaceOf(family), pt, Brushes.Black, Dpi);
                return ft.Width;
            }
            catch { return (text ?? "").Length * pt * 0.6; }
        }

        private static double MeasureHeight(FontFamily family, double pt)
        {
            try
            {
                var ft = new FormattedText("Aa中", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, TypefaceOf(family), pt, Brushes.Black, Dpi);
                return ft.Height;
            }
            catch { return pt * 1.3; }
        }

        /// <summary>纸张尺寸（WPF 逻辑单位，1/96 英寸）。</summary>
        private static Size PageSize(PrintJob job)
        {
            var p = (job.PaperKind ?? "").Trim();
            if (p.Length > 0)
            {
                if (TryParseMm(p, out double wMm, out double hMm))
                    return new Size(MmToUnits(wMm), MmToUnits(hMm));

                switch (p.ToUpperInvariant())
                {
                    case "A0": return new Size(MmToUnits(841), MmToUnits(1189));
                    case "A1": return new Size(MmToUnits(594), MmToUnits(841));
                    case "A2": return new Size(MmToUnits(420), MmToUnits(594));
                    case "A3": return new Size(MmToUnits(297), MmToUnits(420));
                    case "A5": return new Size(MmToUnits(148), MmToUnits(210));
                    case "LETTER": return new Size(InchToUnits(8.5), InchToUnits(11));
                    case "LEGAL": return new Size(InchToUnits(8.5), InchToUnits(14));
                    case "TABLOID": return new Size(InchToUnits(11), InchToUnits(17));
                    case "STATEMENT": return new Size(InchToUnits(5.5), InchToUnits(8.5));
                }
            }
            return new Size(MmToUnits(210), MmToUnits(297));   // 默认 A4
        }

        private static bool IsLandscape(PrintJob job)
        {
            return string.Equals(job.Scale, "landscape", StringComparison.OrdinalIgnoreCase);
        }

        private static double InchToUnits(double inch) { return inch * Dpi; }
        private static double MmToUnits(double mm) { return mm / 25.4 * Dpi; }

        /// <summary>解析 "841mm x 1189mm" 形式的自定义纸张。</summary>
        private static bool TryParseMm(string text, out double w, out double h)
        {
            w = h = 0;
            try
            {
                var parts = text.Split(new[] { 'x', 'X', '×' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return false;
                double a = ParseMm(parts[0]);
                double b = ParseMm(parts[1]);
                if (a <= 0 || b <= 0) return false;
                w = a; h = b;
                return true;
            }
            catch { return false; }
        }

        private static double ParseMm(string s)
        {
            s = s.Trim().ToLowerInvariant().Replace("mm", "").Trim();
            double v;
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v))
                return 0;
            return v;
        }

        private static double ComputeScale(double imgW, double imgH, double boxW, double boxH, string fit)
        {
            if (imgW <= 0 || imgH <= 0 || boxW <= 0 || boxH <= 0) return 1;
            double sx = boxW / imgW;
            double sy = boxH / imgH;
            double s = Math.Min(sx, sy);
            var mode = (fit ?? "fit").ToLowerInvariant();
            switch (mode)
            {
                case "noscale": return 1;
                case "shrink": return Math.Min(s, 1);
                case "compat": return 1;
                default: return s;   // fit
            }
        }

        private static PrintOutcome Fail(string message)
        {
            return new PrintOutcome { Ok = false, Message = message, Detail = message };
        }
    }
}

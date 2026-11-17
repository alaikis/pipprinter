// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>一次打印的结果。</summary>
    public class PrintOutcome
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Engine { get; set; } = "";
    }

    /// <summary>支持的文件类型分组。</summary>
    public static class PrintFileTypes
    {
        public static readonly string[] Pdf = { ".pdf" };
        public static readonly string[] Office = { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".odt" };
        public static readonly string[] Image = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        public static readonly string[] Html = { ".html", ".htm", ".mhtml" };
        public static readonly string[] Text = { ".txt", ".csv", ".log", ".md" };
        public static readonly string[] Svg = { ".svg" };
        public static readonly string[] Xps = { ".xps" };
        public static readonly string[] Ofd = { ".ofd" };

        /// <summary>扩展名 → 内容类别，供路由与文档展示。</summary>
        public static string CategoryOf(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            if (Array.IndexOf(Pdf, ext) >= 0) return "pdf";
            if (Array.IndexOf(Office, ext) >= 0) return "office";
            if (Array.IndexOf(Image, ext) >= 0) return "image";
            if (Array.IndexOf(Html, ext) >= 0) return "html";
            if (Array.IndexOf(Text, ext) >= 0) return "text";
            if (Array.IndexOf(Svg, ext) >= 0) return "svg";
            if (Array.IndexOf(Xps, ext) >= 0) return "xps";
            if (Array.IndexOf(Ofd, ext) >= 0) return "ofd";
            return "unknown";
        }

        public static string[] All
        {
            get
            {
                var list = new List<string>();
                list.AddRange(Pdf); list.AddRange(Office); list.AddRange(Image);
                list.AddRange(Html); list.AddRange(Text);
                list.AddRange(Svg); list.AddRange(Xps); list.AddRange(Ofd);
                return list.ToArray();
            }
        }

        public static string Filter => "可打印文件|" + string.Join(";", Array.ConvertAll(All, x => "*" + x)) +
                                       "|所有文件|*.*";
    }

    /// <summary>
    /// 多类型文件打印服务：按扩展名分流到 SumatraPDF / 系统 Shell 打印动词 / 无头浏览器。
    /// 所有外部程序都通过 ArgumentList 传参，避免命令行注入。
    /// </summary>
    public class ExtendedPrintService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>供模板打印等复用：把 URL 下载为本地字节（失败抛异常）。</summary>
        public static byte[] DownloadBytes(string url)
            => Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        private readonly NativePrintService _native = new NativePrintService();

        /// <summary>
        /// 执行打印。URL 会先下载到 temp 目录；文件类型由扩展名或 Content-Type 推断。
        /// </summary>
        public PrintOutcome Print(PrintJob job)
        {
            if (job == null) return new PrintOutcome { Ok = false, Message = "任务为空" };
            try
            {
                var local = ResolveLocalFile(job);
                var ext = Path.GetExtension(local).ToLowerInvariant();
                var printer = string.IsNullOrWhiteSpace(job.Printer)
                    ? new System.Drawing.Printing.PrinterSettings().PrinterName
                    : job.Printer;

                Tools.Logger(string.Format("打印任务 {0} → {1} (类型 {2}, 打印机 {3})",
                    job.Id, local, ext, printer));

                if (Array.IndexOf(PrintFileTypes.Pdf, ext) >= 0)
                    return PrintWithSumatra(local, job, printer);

                if (Array.IndexOf(PrintFileTypes.Html, ext) >= 0)
                    return PrintHtml(local, job, printer);

                // 图片 / 文本：GDI+ 原生渲染，不依赖 Office / 看图软件 / Shell 关联
                if (Array.IndexOf(PrintFileTypes.Image, ext) >= 0)
                    return _native.PrintImage(local, job, printer);

                if (Array.IndexOf(PrintFileTypes.Text, ext) >= 0)
                    return _native.PrintText(local, job, printer);

                // SVG：无头浏览器渲染为 PDF 后打印
                if (Array.IndexOf(PrintFileTypes.Svg, ext) >= 0)
                    return PrintSvg(local, job, printer);

                // OFD：国产版式文档（电子发票等），转换器转 PDF 后打印，否则交给系统关联程序
                if (Array.IndexOf(PrintFileTypes.Ofd, ext) >= 0)
                    return PrintOfd(local, job, printer);

                // Office：LibreOffice headless 转 PDF 后打印；无 LibreOffice 时退回 Shell
                if (Array.IndexOf(PrintFileTypes.Office, ext) >= 0)
                    return PrintOffice(local, job, printer);

                // XPS：用 WPF 渲染为位图后走 GDI+ 原生管线（不依赖 XPS Viewer）
                if (Array.IndexOf(PrintFileTypes.Xps, ext) >= 0)
                    return PrintXps(local, job, printer);

                // 未知类型：交给系统关联程序兜底
                return PrintWithShellVerb(local, printer);
            }
            catch (Exception ex)
            {
                Tools.Logger("打印失败: " + ex);
                return new PrintOutcome { Ok = false, Message = ex.Message, Detail = ex.ToString() };
            }
        }

        /// <summary>供虚拟打印等外部调用：把任务解析为本地文件（URL 会下载）。</summary>
        public string ResolveLocal(PrintJob job) => ResolveLocalFile(job);

        /// <summary>
        /// 虚拟打印：把任务输出为文件（PDF / XPS），而非发送到实体打印机。
        /// 图片与文本走 GDI+ 原生渲染；PDF 直接复制（无损）；Office/SVG 先转 PDF 再复制。
        /// </summary>
        public PrintOutcome PrintVirtual(PrintJob job, string virtualPrinter, string outPath)
        {
            if (job == null) return new PrintOutcome { Ok = false, Message = "任务为空" };
            try
            {
                var local = ResolveLocalFile(job);
                var ext = Path.GetExtension(local).ToLowerInvariant();
                var cat = PrintFileTypes.CategoryOf(ext);

                switch (cat)
                {
                    case "image":
                        return _native.PrintImageToVirtual(local, job, virtualPrinter, outPath);
                    case "text":
                        return _native.PrintTextToVirtual(local, job, virtualPrinter, outPath);
                    case "pdf":
                        return CopyAsOutput(local, outPath);
                    case "svg":
                        var svgPng = RenderSvgToPng(local);
                        if (svgPng == null)
                            return new PrintOutcome { Ok = false, Message = "SVG 渲染失败，无法输出: " + local };
                        try { return _native.PrintImageToVirtual(svgPng, job, virtualPrinter, outPath); }
                        finally { TryDelete(svgPng); }
                    case "office":
                        var pdf = ConvertToPdf(local, ext);
                        if (pdf == null)
                            return new PrintOutcome { Ok = false, Message = "无法转换为 PDF（需要 LibreOffice）: " + ext };
                        return CopyAsOutput(pdf, outPath);
                    default:
                        return new PrintOutcome { Ok = false, Message = "该类型不支持虚拟打印输出: " + ext };
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("虚拟打印失败: " + ex);
                return new PrintOutcome { Ok = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 输出 XPS 文件。
        /// 图片 / 文本：使用内置 XPS 写入器（不依赖可选功能 Microsoft XPS Document Writer）。
        /// PDF / Office / SVG：内置写入器不覆盖，返回明确提示（避免把 PDF 直接改名成 .xps 造成假文件）。
        /// </summary>
        public PrintOutcome PrintVirtualXps(PrintJob job, string outPath)
        {
            if (job == null) return new PrintOutcome { Ok = false, Message = "任务为空" };
            var writer = new XpsWriterService();
            try
            {
                var local = ResolveLocalFile(job);
                var ext = Path.GetExtension(local).ToLowerInvariant();
                var cat = PrintFileTypes.CategoryOf(ext);

                if (cat == "image") return writer.WriteImage(local, job, outPath);
                if (cat == "text") return writer.WriteText(local, job, outPath);

                return new PrintOutcome
                {
                    Ok = false,
                    Message = "XPS 输出目前支持图片与文本。" + ext + " 类型请改用 PrintToPdf 输出 PDF，"
                        + "或先转换为图片/PDF 后再打印。",
                    Detail = "内置 XPS 写入器覆盖范围: image, text"
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("XPS 输出失败: " + ex);
                return new PrintOutcome { Ok = false, Message = ex.Message };
            }
        }

        private static PrintOutcome CopyAsOutput(string source, string outPath)
        {
            try
            {
                var full = Path.GetFullPath(outPath);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(source, full, true);
                return new PrintOutcome { Ok = true, Engine = "Copy", Message = "已输出到 " + full };
            }
            catch (Exception ex)
            {
                return new PrintOutcome { Ok = false, Message = "输出失败: " + ex.Message };
            }
        }

        /// <summary>把 Office / SVG 转成 PDF（供虚拟打印复用）。失败返回 null。</summary>
        private string ConvertToPdf(string local, string ext)
        {
            if (Array.IndexOf(PrintFileTypes.Office, ext) >= 0)
            {
                var soffice = FindSoffice();
                if (string.IsNullOrEmpty(soffice)) return null;

                var pdf = Path.Combine(SettingsStore.Instance.TempDir,
                    Path.GetFileNameWithoutExtension(local) + ".pdf");
                try { if (File.Exists(pdf)) File.Delete(pdf); }
                catch { /* 忽略 */ }

                var psi = new ProcessStartInfo
                {
                    FileName = soffice,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = SettingsStore.Instance.TempDir
                };
                psi.ArgumentList.Add("--headless");
                psi.ArgumentList.Add("--convert-to");
                psi.ArgumentList.Add("pdf");
                psi.ArgumentList.Add("--outdir");
                psi.ArgumentList.Add(SettingsStore.Instance.TempDir);
                psi.ArgumentList.Add(Path.GetFullPath(local));

                using (var p = Process.Start(psi))
                {
                    if (p == null || !p.WaitForExit(120000) || p.ExitCode != 0) return null;
                }
                return File.Exists(pdf) ? pdf : null;
            }

            // SVG 不再走这里：改为 Svg 库直接渲染位图（见 RenderSvgToPng），
            // headless 浏览器方案在浏览器已有后台进程时会静默失效，已弃用。
            return null;
        }

        /// <summary>把 URL 下载为本地文件；本地路径直接返回。</summary>
        private string ResolveLocalFile(PrintJob job)
        {
            if (job.Source == "url" || IsHttp(job.Target))
            {
                var url = NormalizeUrl(job.Target);
                var uri = new Uri(url);
                byte[] bytes;
                string name;
                string mime;
                using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    mime = resp.Content?.Headers?.ContentType?.MediaType ?? "";
                    name = NameFromResponse(resp, uri);
                }

                // 识别顺序：响应文件名后缀 → 文件字节特征 → 响应 Content-Type。
                // 后缀与内容本身即是依据；三者都拿不到时明确报错，不再落 .bin 交给系统碰运气。
                var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
                if (Array.IndexOf(PrintFileTypes.All, ext) < 0) ext = "";
                if (string.IsNullOrWhiteSpace(ext))
                {
                    var magic = GuessExtensionFromBytes(bytes);
                    if (magic != ".bin") ext = magic;
                }
                if (string.IsNullOrWhiteSpace(ext)) ext = ExtensionFromContentType(mime);
                if (Array.IndexOf(PrintFileTypes.All, ext) < 0) ext = "";
                if (string.IsNullOrWhiteSpace(ext))
                    throw new InvalidOperationException(
                        "无法识别网络文件类型：请提供带可识别扩展名的下载地址，或由服务端返回正确的文件名 / 内容类型；未执行打印");
                var stem = string.IsNullOrWhiteSpace(name) ? "download" : Path.GetFileNameWithoutExtension(name);
                name = stem + ext;
                Tools.Logger(string.Format("网络文件 {0} → {1} (Content-Type {2}, 识别扩展名 {3})",
                    url, name, mime, ext));

                var path = Path.Combine(SettingsStore.Instance.TempDir,
                    Guid.NewGuid().ToString("N") + "_" + SanitizeFileName(name));
                File.WriteAllBytes(path, bytes);
                return path;
            }

            if (!File.Exists(job.Target))
                throw new FileNotFoundException("文件不存在: " + job.Target);
            return Path.GetFullPath(job.Target);
        }

        private static bool IsHttp(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 判断 target 是否为 URL（含协议相对 // 与旧客户端的无协议写法）。
        /// 是则返回规范化 URL，否则返回 null。仅用于判定 Source=url。
        /// </summary>
        public static string NormalizeUrlSafe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            var looksUrl = s.StartsWith("//", StringComparison.Ordinal) ||
                           s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!looksUrl)
            {
                // 本地路径（含盘符、UNC）一律不视为 URL
                if (Path.IsPathRooted(s) || s.StartsWith("\\\\", StringComparison.Ordinal) ||
                    (s.Length > 1 && s[1] == ':')) return null;
                // 兼容旧协议：label 字段可能是 "host/path.pdf"
                if (!s.Contains(".") || s.Contains(Path.DirectorySeparatorChar)) return null;
            }
            try { return NormalizeUrl(s); }
            catch { return null; }
        }

        /// <summary>兼容旧客户端的协议无关 URL（//host/x.pdf）。</summary>
        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("URL 为空");
            var s = raw.Trim();
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            var uri = new Uri(s);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("仅支持 http/https URL");
            return s;
        }

        /// <summary>从 Content-Disposition 或 URL 路径取文件名（含扩展名）。</summary>
        private static string NameFromResponse(HttpResponseMessage resp, Uri uri)
        {
            var cd = resp.Content?.Headers?.ContentDisposition;
            var fn = cd?.FileNameStar ?? cd?.FileName;
            if (!string.IsNullOrWhiteSpace(fn))
            {
                fn = Path.GetFileName(fn.Trim().Trim('"').Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(fn) && Path.HasExtension(fn)) return fn;
            }
            return Path.GetFileName(uri.AbsolutePath);
        }

        /// <summary>Content-Type → 本应用支持的扩展名；无法映射时返回空串。</summary>
        public static string ExtensionFromContentType(string contentType)
        {
            var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            switch (mime)
            {
                case "application/pdf": return ".pdf";
                case "image/png": return ".png";
                case "image/jpeg": case "image/jpg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/bmp": case "image/x-ms-bmp": return ".bmp";
                case "image/tiff": return ".tif";
                case "text/html": case "application/xhtml+xml": return ".html";
                case "text/plain": case "text/csv": return ".txt";
                case "image/svg+xml": return ".svg";
                case "application/msword": return ".doc";
                case "application/vnd.ms-excel": return ".xls";
                case "application/vnd.ms-powerpoint": return ".ppt";
                case "application/vnd.openxmlformats-officedocument.wordprocessingml.document": return ".docx";
                case "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": return ".xlsx";
                case "application/vnd.openxmlformats-officedocument.presentationml.presentation": return ".pptx";
                case "application/vnd.oasis.opendocument.text": return ".odt";
                case "application/rtf": case "text/rtf": return ".rtf";
                case "application/vnd.ms-xpsdocument": case "application/oxps": return ".xps";
                case "application/ofd": return ".ofd";
                default: return "";
            }
        }

        private static bool StartsWithAscii(byte[] b, string s)
        {
            if (b == null || b.Length < s.Length) return false;
            for (int i = 0; i < s.Length; i++) if (b[i] != (byte)s[i]) return false;
            return true;
        }

        /// <summary>按内容魔数推断扩展名，供无扩展名网络文件与内联内容使用。</summary>
        public static string GuessExtensionFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return ".bin";
            if (StartsWithAscii(bytes, "%PDF")) return ".pdf";
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            if (StartsWithAscii(bytes, "GIF87a") || StartsWithAscii(bytes, "GIF89a")) return ".gif";
            if (bytes[0] == 'B' && bytes[1] == 'M') return ".bmp";
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0) ||
                (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0 && bytes[3] == 0x2A)) return ".tif";
            var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512))
                .TrimStart('\uFEFF', ' ', '\r', '\n', '\t').ToLowerInvariant();
            if (head.StartsWith("<!doctype html") || head.StartsWith("<html")) return ".html";
            if (head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg"))) return ".svg";
            return ".bin";
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// 解析 SumatraPDF.exe：优先 lib/driver，其次随包附带的 pdfreader/SumatraPDF.exe。
        /// 找不到时由 DriverCheck 尝试在线下载。
        /// </summary>
        public string ResolveSumatra()
        {
            var primary = Path.Combine(SettingsStore.Instance.DriverDir, "SumatraPDF.exe");
            if (File.Exists(primary)) return primary;

            var bundled = Path.Combine(SettingsStore.Instance.BaseDir, "pdfreader", "SumatraPDF.exe");
            if (File.Exists(bundled))
            {
                File.Copy(bundled, primary, true);
                Tools.Logger("已从随包资源恢复 PDF 打印驱动: " + primary);
                return primary;
            }

            new DriverCheck().EnsureDriver();
            if (File.Exists(primary)) return primary;
            throw new FileNotFoundException("未找到 SumatraPDF.exe，请检查 lib/driver 目录或联网以下载驱动");
        }

        /// <summary>
        /// 纸张参数归一化（对 SumatraPDF -print-settings paper= 的取值）：
        /// A0/A1 超出 SumatraPDF 标准名列表（A2–A6/letter/legal/tabloid/statement），
        /// 换算为 ISO 216 自定义尺寸毫米串；auto 与其余值（标准名/驱动名/自定义尺寸）原样透传。
        /// 大幅面出图实际能否兑现取决于打印机驱动是否支持该幅面（绘图仪）。
        /// </summary>
        internal static string NormalizePaper(string paper)
        {
            var p = (paper ?? "").Trim();
            if (p.Length == 0) return p;
            switch (p.ToUpperInvariant())
            {
                case "A0": return "841mm x 1189mm";
                case "A1": return "594mm x 841mm";
                default: return p;
            }
        }

        /// <summary>
        /// 直接用 SumatraPDF 打印（PDF/图片），绕过系统 Shell 关联。
        /// 供条码标签等本应用生成的中间文件使用，打印行为可控且静默。
        /// </summary>
        public PrintOutcome PrintDirect(string file, PrintJob job, string printer)
        {
            return PrintWithSumatra(file, job, printer);
        }

        /// <summary>把本应用的 fit 取值归一化为 SumatraPDF 支持的值（compat 是历史别名）。</summary>
        private static string SumatraFit(string fit)
        {
            switch ((fit ?? "fit").Trim().ToLowerInvariant())
            {
                case "noscale": case "shrink": case "fit": case "stretch": return (fit ?? "fit").Trim().ToLowerInvariant();
                default: return "fit";
            }
        }

        /// <summary>双面模式映射：simplex / duplexlong / duplexshort。</summary>
        private static string SumatraDuplex(string duplex)
        {
            switch ((duplex ?? "simplex").Trim().ToLowerInvariant())
            {
                case "duplex": case "duplexlong": case "long": case "vertical": return "duplexlong";
                case "duplexshort": case "short": case "horizontal": return "duplexshort";
                default: return "simplex";
            }
        }

        private PrintOutcome PrintWithSumatra(string file, PrintJob job, string printer)
        {
            var exe = ResolveSumatra();
            // 下列取值均来自 SumatraPDF 官方 -print-settings 文档，不传未支持的参数。
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(job.Pages) ? "1-" : job.Pages,
                SumatraFit(job.Fit),
                string.Equals(job.Scale, "landscape", StringComparison.OrdinalIgnoreCase) ? "landscape" : "portrait",
                Math.Max(1, job.Copies) + "x"
            };
            if (!string.IsNullOrWhiteSpace(job.PaperKind))
                parts.Add("paper=" + NormalizePaper(job.PaperKind));
            parts.Add(SumatraDuplex(job.Duplex));
            if (job.Color.HasValue) parts.Add(job.Color.Value ? "color" : "monochrome");
            parts.Add(job.Collate ? "collate" : "nocollate");
            if (!string.IsNullOrWhiteSpace(job.Bin)) parts.Add("bin=" + job.Bin);
            if (job.Dpi > 0) parts.Add("dpi=" + job.Dpi);
            var parity = (job.PageOrder ?? "").Trim().ToLowerInvariant();
            if (parity == "even" || parity == "odd") parts.Add(parity);
            var settings = string.Join(",", parts);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-silent");
            psi.ArgumentList.Add("-exit-when-done");
            psi.ArgumentList.Add("-print-settings");
            psi.ArgumentList.Add(settings);
            psi.ArgumentList.Add("-print-to");
            psi.ArgumentList.Add(printer);
            psi.ArgumentList.Add(file);

            using (var p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("无法启动 SumatraPDF");
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(120000)) p.Kill(true);
                var detail = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();
                if (p.ExitCode != 0)
                    return new PrintOutcome
                    {
                        Ok = false, Engine = "SumatraPDF",
                        Message = "SumatraPDF 退出码 " + p.ExitCode, Detail = detail
                    };
                return new PrintOutcome
                {
                    Ok = true, Engine = "SumatraPDF",
                    Message = "已提交到打印机 " + printer, Detail = detail
                };
            }
        }

        /// <summary>
        /// Office / 图片 / 文本：使用系统注册的 print Shell 动词，由关联应用接管打印。
        /// 不会拼接命令行，因此文件名中的特殊字符是安全的。
        /// </summary>
        private PrintOutcome PrintWithShellVerb(string file, string printer)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Verb = "print",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p != null && !p.WaitForExit(120000))
                    {
                        // 部分应用由 Shell 打印后即刻退出；超时不视为失败
                        Tools.Logger("Shell 打印进程未退出，交由系统后台完成: " + file);
                    }
                    return new PrintOutcome
                    {
                        Ok = true, Engine = "ShellVerb",
                        Message = "已通过系统关联程序提交打印到 " + printer,
                        Detail = "若未出纸，请确认已安装可打印该类型文件的应用（Office / WPS / 图片查看器）"
                    };
                }
            }
            catch (Exception ex)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                Tools.Logger("Shell 打印失败(" + ext + "): " + ex.Message);
                return new PrintOutcome
                {
                    Ok = false,
                    Engine = "ShellVerb",
                    Message = "没有可打印 " + ext + " 的系统关联程序（" + Path.GetFileName(file) + "）。"
                        + "请安装对应的阅读器/办公软件，或在设置中配置转换器后重试。",
                    Detail = ex.Message
                };
            }
        }

        /// <summary>
        /// HTML：优先用 Edge/Chrome 无头模式转 PDF，再走 SumatraPDF；
        /// 找不到浏览器时退回系统关联程序打印。
        /// </summary>
        private PrintOutcome PrintHtml(string file, PrintJob job, string printer)
        {
            var browser = FindBrowser();
            if (string.IsNullOrEmpty(browser))
                return PrintWithShellVerb(file, printer);

            var pdf = Path.Combine(SettingsStore.Instance.TempDir,
                Guid.NewGuid().ToString("N") + ".pdf");
            var psi = new ProcessStartInfo
            {
                FileName = browser,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--disable-gpu");
            psi.ArgumentList.Add("--no-pdf-header-footer");
            psi.ArgumentList.Add("--print-to-pdf=" + pdf);
            psi.ArgumentList.Add(new Uri(Path.GetFullPath(file)).AbsoluteUri);

            using (var p = Process.Start(psi))
            {
                if (p == null || !p.WaitForExit(60000))
                    return PrintWithShellVerb(file, printer);
            }
            if (!File.Exists(pdf)) return PrintWithShellVerb(file, printer);

            var job2 = job.Clone();
            job2.Target = pdf;
            job2.Source = "file";
            return PrintWithSumatra(pdf, job2, printer);
        }

        /// <summary>
        /// SVG：用 Svg 库直接渲染为位图，再走 GDI+ 原生打印管线。
        /// 相比 headless Edge/Chrome 更可靠：不依赖浏览器后台进程、用户配置与会话状态。
        /// 渲染失败时退回系统关联程序。
        /// </summary>
        private PrintOutcome PrintSvg(string file, PrintJob job, string printer)
        {
            var png = RenderSvgToPng(file);
            if (string.IsNullOrEmpty(png))
            {
                Tools.Logger("SVG 渲染失败，改用系统关联程序打印");
                return PrintWithShellVerb(file, printer);
            }
            try
            {
                var outcome = _native.PrintImage(png, job, printer);
                outcome.Engine = "SVG+" + outcome.Engine;
                return outcome;
            }
            finally { TryDelete(png); }
        }

        /// <summary>
        /// 把 SVG 渲染为临时 PNG（长边约 2400px，约合 300 DPI 下 8 英寸）。
        /// 失败返回 null。
        /// </summary>
        private static string RenderSvgToPng(string file)
        {
            try
            {
                var svg = Svg.SvgDocument.Open(file);
                if (svg == null) return null;

                double baseW = svg.Width > 0 ? svg.Width : svg.ViewBox.Width;
                double baseH = svg.Height > 0 ? svg.Height : svg.ViewBox.Height;
                if (baseW <= 0 || baseH <= 0) { baseW = 400; baseH = 300; }

                // 长边 2400px，并限制总像素防止超大位图
                double scale = 2400.0 / Math.Max(baseW, baseH);
                while (baseW * scale * baseH * scale > 24000000 && scale > 0.1)
                    scale *= 0.85;

                int w = Math.Max(1, (int)Math.Round(baseW * scale));
                int h = Math.Max(1, (int)Math.Round(baseH * scale));

                var path = Path.Combine(SettingsStore.Instance.TempDir,
                    "svg_" + Guid.NewGuid().ToString("N") + ".png");
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.White);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        svg.Draw(g);
                    }
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                Tools.Logger("SVG 渲染异常: " + ex.Message);
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { /* 临时文件清理失败不影响主流程 */ }
        }

        /// <summary>
        /// XPS 文档：用 WPF 的 XpsDocument 渲染每页为位图，再走 GDI+ 原生打印管线。
        /// 不依赖系统是否安装 XPS Viewer。渲染失败时退回系统关联程序。
        /// </summary>
        private PrintOutcome PrintXps(string file, PrintJob job, string printer)
        {
            List<string> pages = null;
            try { pages = new XpsWriterService().RenderPages(file); }
            catch (Exception ex) { Tools.Logger("XPS 渲染异常: " + ex.Message); }

            if (pages == null || pages.Count == 0)
            {
                Tools.Logger("XPS 渲染失败或文档为空，改用系统关联程序打印");
                return PrintWithShellVerb(file, printer);
            }

            try
            {
                var outcome = _native.PrintImages(pages, job, printer);
                outcome.Engine = "XPS+" + outcome.Engine;
                return outcome;
            }
            finally
            {
                foreach (var p in pages) TryDelete(p);
            }
        }

        /// <summary>
        /// Office：优先用 LibreOffice headless 转 PDF（本机无需安装 Office/WPS 即可打印），
        /// 转换失败或未安装时退回系统关联程序。
        /// </summary>
        private PrintOutcome PrintOffice(string file, PrintJob job, string printer)
        {
            var pdf = ConvertToPdf(file, Path.GetExtension(file).ToLowerInvariant());
            if (pdf != null)
            {
                var job2 = job.Clone();
                job2.Target = pdf;
                job2.Source = "file";
                var outcome = PrintWithSumatra(pdf, job2, printer);
                outcome.Engine = "LibreOffice+" + outcome.Engine;
                return outcome;
            }
            Tools.Logger("未找到 LibreOffice，改用系统关联程序打印 Office 文件");
            return PrintWithShellVerb(file, printer);
        }

        /// <summary>
        /// OFD（国产版式文档，电子发票/电子公文）：优先调用配置的转换器转 PDF，
        /// 否则尝试 LibreOffice（部分发行版带 OFD 插件），最后退回系统关联程序（WPS / 数科阅读器）。
        /// </summary>
        private PrintOutcome PrintOfd(string file, PrintJob job, string printer)
        {
            var converter = SettingsStore.Instance.Current.OfdConverter;
            if (!string.IsNullOrWhiteSpace(converter) && File.Exists(converter))
            {
                var pdf = Path.Combine(SettingsStore.Instance.TempDir,
                    Guid.NewGuid().ToString("N") + ".pdf");
                var psi = new ProcessStartInfo
                {
                    FileName = converter,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = SettingsStore.Instance.TempDir
                };
                // 约定：<converter> <输入.ofd> <输出.pdf>
                psi.ArgumentList.Add(Path.GetFullPath(file));
                psi.ArgumentList.Add(pdf);
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        if (p != null && p.WaitForExit(60000) && p.ExitCode == 0 && File.Exists(pdf))
                        {
                            var job2 = job.Clone();
                            job2.Target = pdf;
                            job2.Source = "file";
                            var outcome = PrintWithSumatra(pdf, job2, printer);
                            outcome.Engine = "OFD+" + outcome.Engine;
                            return outcome;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tools.Logger("OFD 转换失败: " + ex.Message);
                }
            }

            // 无转换器时交给系统关联程序（WPS、数科 OFD 阅读器等支持 OFD 打印）
            Tools.Logger("未配置 OFD 转换器，改用系统关联程序打印 OFD");
            return PrintWithShellVerb(file, printer);
        }

        /// <summary>
        /// 查找 LibreOffice soffice.exe（headless 转换 Office/OFD 用）。
        /// 优先级：lib/office/（内置转换器）→ 随包 office-converter/ → 系统 LibreOffice → 在线获取。
        /// </summary>
        private static string FindSoffice()
        {
            // 1) 内置转换器（随包注入或在线下载后落在 lib/office/）
            var builtin = Path.Combine(SettingsStore.Instance.BaseDir, "lib", "office", "soffice.exe");
            if (File.Exists(builtin)) return builtin;

            // 2) 随包资源目录
            var bundled = Path.Combine(SettingsStore.Instance.BaseDir, "office-converter", "soffice.exe");
            if (File.Exists(bundled)) return bundled;

            // 3) 系统已安装的 LibreOffice
            var systemCandidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\LibreOffice\program\soffice.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\LibreOffice\program\soffice.exe"),
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
            };
            foreach (var c in systemCandidates) if (File.Exists(c)) return c;

            // 4) 在线获取（DriverCheck.EnsureOfficeConverter 内部走完整校验链路）
            try
            {
                var path = new DriverCheck().EnsureOfficeConverter();
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            catch (Exception ex)
            {
                Tools.Logger("在线获取 Office 转换器失败: " + ex.Message);
            }
            return null;
        }

        private static string FindBrowser()
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe")
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }
    }
}

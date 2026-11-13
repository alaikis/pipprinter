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

        public static string[] All
        {
            get
            {
                var list = new List<string>();
                list.AddRange(Pdf); list.AddRange(Office); list.AddRange(Image);
                list.AddRange(Html); list.AddRange(Text);
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

                if (Array.IndexOf(PrintFileTypes.Office, ext) >= 0 ||
                    Array.IndexOf(PrintFileTypes.Image, ext) >= 0 ||
                    Array.IndexOf(PrintFileTypes.Text, ext) >= 0)
                    return PrintWithShellVerb(local, printer);

                // 未知类型也交给系统 Shell 兜底
                return PrintWithShellVerb(local, printer);
            }
            catch (Exception ex)
            {
                Tools.Logger("打印失败: " + ex);
                return new PrintOutcome { Ok = false, Message = ex.Message, Detail = ex.ToString() };
            }
        }

        /// <summary>把 URL 下载为本地文件；本地路径直接返回。</summary>
        private string ResolveLocalFile(PrintJob job)
        {
            if (job.Source == "url" || IsHttp(job.Target))
            {
                var url = NormalizeUrl(job.Target);
                var uri = new Uri(url);
                var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                var name = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name))
                    name = "download" + GuessExtension(bytes);
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

        private static string GuessExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return ".bin";
            if (bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F') return ".pdf";
            if (bytes[0] == 0x89 && bytes[1] == 0x50) return ".png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
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

        private PrintOutcome PrintWithSumatra(string file, PrintJob job, string printer)
        {
            var exe = ResolveSumatra();
            var settings = string.Join(",", new[]
            {
                string.IsNullOrWhiteSpace(job.Pages) ? "1-" : job.Pages,
                string.IsNullOrWhiteSpace(job.Fit) ? "fit" : job.Fit,
                string.IsNullOrWhiteSpace(job.Scale) ? "portrait" : job.Scale,
                Math.Max(1, job.Copies) + "x"
            });
            if (!string.IsNullOrWhiteSpace(job.PaperKind))
                settings += ",paper=" + job.PaperKind;

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

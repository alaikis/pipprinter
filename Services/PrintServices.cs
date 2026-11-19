// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using DuoAssistant.Models;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 所有对外命令的实现。每个公开实例方法必须签名 (string args) → string，
    /// 供 CommandDispatcher 通过反射分派（WebSocket / REST / UI 共用）。
    /// 返回值统一为 Result 结构的 JSON 字符串。
    /// </summary>
    public class PrintServices
    {
        private readonly ExtendedPrintService _extended = new ExtendedPrintService();
        private readonly PrinterService _printers = new PrinterService();
        private readonly DiskCleanerService _cleaner = new DiskCleanerService();

        // ---------------------------------------------------------------- 打印

        /// <summary>
        /// 原有 PDF 网络打印命令（保持兼容）。现在复用多类型打印引擎。
        /// 支持 label 为 http(s) / 协议相对 URL / 本地路径。
        /// </summary>
        public string PrintPdf(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var label = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("缺少 target 字段");

                var job = BuildJob(dt, label);
                var outcome = _extended.Print(job);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "Print", data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    label,
                    printer = job.Printer,
                    engine = EngineCategory(outcome.Engine),
                    message = outcome.Message
                }, cmd: "Print", msg: "print successful!");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "Print");
            }
        }

        /// <summary>
        /// 条码 / 二维码生成并直接打印（同步）。
        /// 参数：value（必填）、format（qr/code128/code39/ean13/…，默认 qr）、
        /// width/height（px，默认 400）、caption（可选文字）、printer、copies、paperKind。
        /// 生成 PNG 后经 SumatraPDF 直出，不经过系统 Shell，行为可控且静默。
        /// </summary>
        public string PrintBarcode(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var value = Str(dt, "value") ?? Str(dt, "text");
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("缺少 value 字段");

                var png = BarcodeGenerator.Generate(
                    value,
                    Str(dt, "format") ?? Str(dt, "barcodeType") ?? "qr",
                    Int(dt, "width", 400),
                    Int(dt, "height", 400),
                    Str(dt, "caption"));

                var job = BuildJob(dt, png);
                var printer = string.IsNullOrWhiteSpace(job.Printer)
                    ? new System.Drawing.Printing.PrinterSettings().PrinterName
                    : job.Printer;
                var outcome = _extended.PrintDirect(png, job, printer);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "PrintBarcode", data: new { detail = outcome.Detail, image = png });
                return Result.success(data: new
                {
                    value, image = png, printer, engine = EngineCategory(outcome.Engine),
                    message = outcome.Message
                }, cmd: "PrintBarcode", msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "PrintBarcode");
            }
        }

        /// <summary>打印任意受支持类型的文件 / URL（同步）。</summary>
        public string PrintFile(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 target 字段");
                var job = BuildJob(dt, target);
                var outcome = _extended.Print(job);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "PrintFile", data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    path = target, printer = job.Printer, engine = EngineCategory(outcome.Engine),
                    message = outcome.Message, supportedTypes = PrintFileTypes.All
                }, cmd: "PrintFile", msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "PrintFile");
            }
        }


        /// <summary>
        /// 自动打印：不指定 cmd 时的默认行为（也可显式写 cmd="Auto" / "AutoPrint"）。
        /// 依据请求中真实出现的内容识别来源（内联内容 / 本地文件 / 网络 URL）与文件形式，
        /// 再交给对应的真实打印命令；返回 data.auto 附识别结果，便于调用方核对。
        /// 不猜测条码、ESC/POS 等专用命令，避免把普通文本误判成小票指令。
        /// </summary>
        public string AutoPrint(string args)
        {
            try
            {
                var dt = ParseObject(args);

                // 模板：显式 html / templateUrl 字段 → 模板打印
                if (!string.IsNullOrWhiteSpace(Str(dt, "html")) || !string.IsNullOrWhiteSpace(Str(dt, "templateUrl")))
                    return Decorate(TemplatePrint(args), "TemplatePrint", "template", "请求含 html / templateUrl 模板字段");

                // 内联内容：content / base64 / data URI → 落临时文件后按内容识别
                var inline = Str(dt, "content") ?? Str(dt, "base64");
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    var inlineIsBase64 = !string.IsNullOrWhiteSpace(Str(dt, "base64")) ||
                        string.Equals(Str(dt, "encoding"), "base64", StringComparison.OrdinalIgnoreCase);
                    var inlinePath = SaveInlineContent(dt, inline, inlineIsBase64);
                    var ext = Path.GetExtension(inlinePath);
                    return Decorate(PrintFile(WithTarget(dt, inlinePath)), "PrintFile",
                        PrintFileTypes.CategoryOf(ext),
                        "请求含内联内容，按文件名后缀 / 文件字节特征识别为 " + ext + " 并自动处理");
                }

                // 文件 / URL：交给 PrintFile，由下载层按文件名后缀 / 文件字节特征 / 响应类型识别
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target))
                    return Result.error("无法识别打印内容：请提供 target（本机路径或 http(s) 地址），或 content / base64 内联内容",
                        400, "Auto");

                var isUrl = ExtendedPrintService.NormalizeUrlSafe(target) != null;
                var probe = isUrl
                    ? new Uri(ExtendedPrintService.NormalizeUrl(target)).AbsolutePath
                    : target;
                var category = PrintFileTypes.CategoryOf(Path.GetExtension(probe));
                return Decorate(PrintFile(WithTarget(dt, target)), "PrintFile", category,
                    isUrl ? "识别为网络文件，下载后按文件形式自动处理"
                          : "识别为本地文件，按文件形式自动处理");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, 400, "Auto");
            }
        }

        /// <summary>把识别结果写进命令返回，不改变原有 code / data 结构。</summary>
        private static string Decorate(string result, string resolved, string category, string detectedBy)
        {
            try
            {
                var o = JObject.Parse(result);
                var d = o["data"] as JObject ?? new JObject();
                d["auto"] = new JObject
                {
                    ["resolved"] = resolved,
                    ["category"] = category,
                    ["detectedBy"] = detectedBy
                };
                o["data"] = d;
                return o.ToString(Formatting.None);
            }
            catch { return result; }
        }

        /// <summary>
        /// 统一解析打印目标：对外主推 target，历史字段降级为兼容别名。
        /// 所有打印命令共用，保证「只传一个 target」在显式命令与 auto 下都成立。
        /// </summary>
        private static string ResolveTarget(JObject dt) =>
            Str(dt, "target") ?? Str(dt, "path") ?? Str(dt, "label")
            ?? Str(dt, "url") ?? Str(dt, "file");

        /// <summary>把目标统一写回 target，供下游命令 / BuildJob 使用；历史别名与内联字段一并清除。</summary>
        private static string WithTarget(JObject dt, string target)
        {
            var copy = (JObject)dt.DeepClone();
            copy.Remove("path"); copy.Remove("url"); copy.Remove("label"); copy.Remove("file");
            copy.Remove("content"); copy.Remove("base64"); copy.Remove("encoding");
            copy.Remove("contentType"); copy.Remove("filename"); copy.Remove("fileName");
            copy["target"] = target;
            return copy.ToString(Formatting.None);
        }

        /// <summary>
        /// 把内联内容（纯文本 / base64 / data URI）写成临时文件，返回路径。
        /// 类型只依据「文件名后缀」与「文件字节特征」判定，不采信单独传入的 contentType 字段。
        /// base64 内容两处都无法识别、又没给带扩展名的 filename 时直接报错，
        /// 不落 .bin、不交给系统关联程序碰运气。
        /// 非 base64 的纯文本按 .txt 处理（编码事实，不是猜测）；
        /// data URI 头部自带的媒体类型属于内容元信息，可作为最后一级依据。
        /// </summary>
        private static string SaveInlineContent(JObject dt, string raw, bool isBase64)
        {
            string declared = null;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = raw.IndexOf(',');
                if (comma <= 5) throw new ArgumentException("data URI 格式不正确");
                var head = raw.Substring(5, comma - 5);
                isBase64 = head.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0;
                var semi = head.IndexOf(';');
                declared = semi > 0 ? head.Substring(0, semi) : head;
                raw = raw.Substring(comma + 1);
            }

            byte[] bytes;
            if (isBase64)
            {
                try { bytes = Convert.FromBase64String(raw); }
                catch { throw new ArgumentException("base64 内容无法解码"); }
            }
            else bytes = System.Text.Encoding.UTF8.GetBytes(raw);

            var fn = Str(dt, "filename") ?? Str(dt, "fileName");
            var fromName = string.IsNullOrWhiteSpace(fn) ? "" : Path.GetExtension(fn).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(fromName) && Array.IndexOf(PrintFileTypes.All, fromName) < 0)
                fromName = "";

            var magic = ExtendedPrintService.GuessExtensionFromBytes(bytes);
            var fromContent = magic == ".bin" ? "" : magic;

            var ext = !string.IsNullOrWhiteSpace(fromName) ? fromName
                    : !string.IsNullOrWhiteSpace(fromContent) ? fromContent
                    : "";
            if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(declared))
                ext = ExtendedPrintService.ExtensionFromContentType(declared);
            if (!string.IsNullOrWhiteSpace(ext) && Array.IndexOf(PrintFileTypes.All, ext) < 0) ext = "";

            if (string.IsNullOrWhiteSpace(ext) && !isBase64) ext = ".txt";
            if (string.IsNullOrWhiteSpace(ext))
                throw new ArgumentException(
                    "无法识别内联内容的文件类型：内容中不含可识别的文件特征，请提供 filename（含扩展名，如 label.pdf / label.png / label.docx）");

            var path = Path.Combine(SettingsStore.Instance.TempDir,
                "auto_" + Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // ------------------------------------------------ 虚拟打印（输出 PDF / XPS 文件）

        /// <summary>打印到 PDF 文件（Microsoft Print to PDF 虚拟打印机）。</summary>
        public string PrintToPdf(string args) => VirtualPrint(args, "PrintToPdf", NativePrintService.PdfVirtualPrinter, ".pdf");

        /// <summary>打印到 XPS 文件（Microsoft XPS Document Writer 虚拟打印机）。</summary>
        /// <summary>
        /// 打印到 XPS 文件。图片/文本用内置写入器（无需可选功能），其余类型回退虚拟打印机。
        /// </summary>
        public string PrintToXps(string args)
        {
            const string cmd = "PrintToXps";
            try
            {
                var dt = ParseObject(args);
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 target 字段");

                var outPath = Str(dt, "output") ?? Str(dt, "outPath");
                if (string.IsNullOrWhiteSpace(outPath))
                    outPath = Path.Combine(SettingsStore.Instance.TempDir,
                        "virtual_" + Guid.NewGuid().ToString("N") + ".xps");

                var job = BuildJob(dt, target);
                var outcome = _extended.PrintVirtualXps(job, outPath);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: cmd, data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    output = Path.GetFullPath(outPath),
                    engine = EngineCategory(outcome.Engine),
                    message = outcome.Message
                }, cmd: cmd, msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        private string VirtualPrint(string args, string cmd, string virtualPrinter, string defaultExt)
        {
            try
            {
                var dt = ParseObject(args);
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 target 字段");

                var outPath = Str(dt, "output") ?? Str(dt, "outPath");
                if (string.IsNullOrWhiteSpace(outPath))
                    outPath = Path.Combine(SettingsStore.Instance.TempDir,
                        "virtual_" + Guid.NewGuid().ToString("N") + defaultExt);

                var job = BuildJob(dt, target);
                var outcome = _extended.PrintVirtual(job, virtualPrinter, outPath);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: cmd, data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    output = Path.GetFullPath(outPath),
                    engine = EngineCategory(outcome.Engine),
                    message = outcome.Message
                }, cmd: cmd, msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        // ------------------------------------------------ ESC/POS 热敏小票

        /// <summary>
        /// ESC/POS 原始打印：文本 / 一维条码 / 二维码直接发送字节流。
        /// printer 支持本地打印机名（Win32 RAW）或 host:port（TCP 9100 网络打印机）。
        /// </summary>
        public string PrintEscPos(string args)
        {
            const string cmd = "PrintEscPos";
            try
            {
                var dt = ParseObject(args);
                var printer = Str(dt, "printer") ?? SettingsStore.Instance.Current.DefaultPrinter ?? "";
                var text = Str(dt, "text") ?? "";
                var encoding = Str(dt, "encoding") ?? "GB2312";

                var pos = new EscPos(encoding);
                pos.Initialize();
                pos.Align(Int(dt, "align", 0));
                pos.Bold(Bool(dt, "bold", false));

                if (!string.IsNullOrEmpty(text))
                    foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                        pos.TextLine(line);

                var barcode = Str(dt, "barcode") ?? Str(dt, "barcodeValue");
                if (!string.IsNullOrEmpty(barcode))
                {
                    pos.Align(1);
                    pos.Barcode(barcode,
                        Str(dt, "barcodeFormat") ?? Str(dt, "format") ?? "code128",
                        Int(dt, "barcodeHeight", 50),
                        Int(dt, "barcodeWidth", 2));
                    pos.Align(Int(dt, "align", 0));
                }

                var qrcode = Str(dt, "qrcode") ?? Str(dt, "qrValue");
                if (!string.IsNullOrEmpty(qrcode))
                {
                    pos.Align(1);
                    pos.QrCode(qrcode, Int(dt, "qrSize", 4));
                    pos.Align(Int(dt, "align", 0));
                }

                if (Bool(dt, "cashDrawer", false)) pos.CashDrawer();

                pos.Feed(Int(dt, "feed", 3));
                if (Bool(dt, "cut", true)) pos.Cut(Str(dt, "cutMode") ?? "full");

                var data = pos.ToArray();
                var outcome = new EscPosService().Print(printer, data);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: cmd, data: new { detail = outcome.Detail });

                return Result.success(data: new
                {
                    printer,
                    bytes = data.Length,
                    engine = EngineCategory(outcome.Engine),
                    message = outcome.Message,
                    dump = new EscPosService().SaveToFile(data)
                }, cmd: cmd, msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        /// <summary>
        /// 模板打印（模板+数据模式）：
        ///   template —— 内联 HTML 文本、本地模板文件路径、或模板 URL（CDN/S3 直链）；
        ///   data     —— {{key}} 占位符 → 值 映射，值自动做 HTML 转义（防破版/注入）；
        ///   paperKind/copies/fit/scale/printer —— 打印参数（可选）。
        /// 渲染后入队打印。
        /// </summary>
        public string TemplatePrint(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var template = Str(dt, "template");
                if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("缺少 template 字段");

                string rendered;
                string path;
                bool isHtml;
                string templateOrigin = "inline";

                var templateUrl = ExtendedPrintService.NormalizeUrlSafe(template);
                if (templateUrl != null)
                {
                    // ① template 是 URL（CDN/S3 直链模板）：下载到 temp 再渲染
                    templateOrigin = "url";
                    rendered = DownloadTemplateToText(templateUrl);
                    path = Path.Combine(SettingsStore.Instance.TempDir,
                        Guid.NewGuid().ToString("N") + ".html");
                    isHtml = true;
                }
                else if (File.Exists(template))
                {
                    // ② 本地模板文件路径
                    templateOrigin = "file";
                    path = template;
                    rendered = File.ReadAllText(path);
                    isHtml = Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // ③ 内联 HTML/文本
                    templateOrigin = "inline";
                    rendered = template;
                    isHtml = rendered.IndexOf("<", StringComparison.Ordinal) >= 0 &&
                             rendered.IndexOf(">", StringComparison.Ordinal) >= 0;
                    path = Path.Combine(SettingsStore.Instance.TempDir,
                        Guid.NewGuid().ToString("N") + (isHtml ? ".html" : ".txt"));
                }

                // 参数替换：值做 HTML 转义，避免订单数据里的 < & " 破坏版面或注入
                int replaced = 0;
                if (dt["data"] is JObject data)
                {
                    foreach (var kv in data.Properties())
                    {
                        var value = HtmlEncodeTemplateValue(kv.Value);
                        if (rendered.Contains("{{" + kv.Name + "}}", StringComparison.Ordinal))
                        {
                            rendered = rendered.Replace("{{" + kv.Name + "}}", value);
                            replaced++;
                        }
                    }
                }
                if (!File.Exists(path)) File.WriteAllText(path, rendered);

                Tools.Logger(string.Format("模板打印 {0}: 来源 {1}, 替换 {2}/{3} 个占位符",
                    dt["jobId"]?.ToString() ?? "-", templateOrigin, replaced,
                    dt["data"] is JObject d ? d.Count : 0));

                var job = BuildJob(dt, path);
                var queued = PrintQueueService.Instance.Enqueue(job);
                return Result.success(data: new
                {
                    jobId = queued.Id,
                    renderedPath = path,
                    isHtml,
                    templateOrigin,
                    replaced
                },
                    cmd: "TemplatePrint", msg: "模板已渲染并入队");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "TemplatePrint");
            }
        }

        /// <summary>把 URL 模板下载为本地文本（复用 HttpClient 基建），返回模板内容。</summary>
        private static string DownloadTemplateToText(string url)
        {
            var bytes = ExtendedPrintService.DownloadBytes(url);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>把 JSON 值转为 HTML 安全文本（对象/数组序列化后转义，标量直接转义）。</summary>
        private static string HtmlEncodeTemplateValue(JToken value)
        {
            var raw = value == null || value.Type == JTokenType.Null ? ""
                : value.Type == JTokenType.Object || value.Type == JTokenType.Array
                    ? value.ToString()
                    : value.ToString();
            return System.Net.WebUtility.HtmlEncode(raw);
        }

        // ---------------------------------------------------------------- 队列

        public string QueuePrint(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 target 字段");
                var job = BuildJob(dt, target);
                var queued = PrintQueueService.Instance.Enqueue(job);
                return Result.success(data: new { jobId = queued.Id, job = queued },
                    cmd: "QueuePrint", msg: "已加入打印队列");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "QueuePrint");
            }
        }

        public string GetQueue(string args)
        {
            var queue = PrintQueueService.Instance;
            return Result.success(data: new { jobs = queue.GetAll(), summary = queue.Summary() },
                cmd: "GetQueue");
        }

        public string RetryJob(string args)
        {
            var id = Str(ParseObject(args), "id") ?? Str(ParseObject(args), "jobId");
            if (string.IsNullOrWhiteSpace(id)) return Result.error("缺少 id 字段", cmd: "RetryJob");
            var ok = PrintQueueService.Instance.Retry(id);
            return ok
                ? Result.success(data: new { id }, cmd: "RetryJob", msg: "任务已重新入队")
                : Result.error("任务不存在", 404, "RetryJob");
        }

        public string CancelJob(string args)
        {
            var id = Str(ParseObject(args), "id");
            if (string.IsNullOrWhiteSpace(id)) return Result.error("缺少 id 字段", cmd: "CancelJob");
            var ok = PrintQueueService.Instance.Cancel(id);
            return ok
                ? Result.success(data: new { id }, cmd: "CancelJob", msg: "任务已取消")
                : Result.error("任务不存在或正在打印", 400, "CancelJob");
        }

        public string ClearQueue(string args)
        {
            var dt = ParseObject(args);
            var finishedOnly = Bool(dt, "finishedOnly", true);
            var removed = PrintQueueService.Instance.Clear(finishedOnly);
            return Result.success(data: new { removed, finishedOnly }, cmd: "ClearQueue");
        }

        // ------------------------------------------------------------ 打印机

        /// <summary>保留旧结构（printers: string[]、primary），并附 detail。</summary>
        public string GetPrinterList(string args)
        {
            var names = _printers.GetNames();
            return Result.success(data: new
            {
                printers = names,
                primary = _printers.GetDefault(),
                detail = _printers.GetInstalled()
            }, cmd: "GetPrinterList");
        }

        public string GetPrinterDetail(string args) =>
            Result.success(data: _printers.GetInstalled(), cmd: "GetPrinterDetail");

        /// <summary>
        /// 只读：扫描局域网打印机，叠加 WMI 已安装网络打印机 + SSDP/WSD + mDNS(IPP/IPPS/raw)。
        /// 只发标准发现报文、不装驱动、不改系统。参数 timeoutMs(500-8000)、methods(installed,ssdp,mdns)。
        /// 结果仅用于识别设备；实际使用仍需 Windows 添加网络打印机并安装厂商驱动。
        /// </summary>
        public string ScanLanPrinters(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var timeout = dt["timeoutMs"]?.Value<int>() ?? 1500;
                var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var methodsToken = dt["methods"];
                if (methodsToken is JArray arr && arr.Count > 0)
                    foreach (var m in arr) methods.Add(m.ToString());
                else if (methodsToken != null && !string.IsNullOrWhiteSpace(methodsToken.ToString()))
                    foreach (var m in methodsToken.ToString().Split(','))
                        if (!string.IsNullOrWhiteSpace(m)) methods.Add(m.Trim());

                var result = new LanPrinterScanner().Scan(timeout, methods);
                return Result.success(data: result, cmd: "ScanLanPrinters");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "ScanLanPrinters");
            }
        }

        /// <summary>
        /// 只读：检查本机打印机与驱动是否可用。
        /// 只报告状态，不下载、不安装、不修改任何驱动；needsDriver 以 PrinterSettings.IsValid 为真实依据。
        /// </summary>
        public string CheckPrinterDriver(string args)
        {
            try
            {
                var items = _printers.GetInstalled().Select(p => new
                {
                    name = p.Name,
                    driver = p.Driver,
                    port = p.Port,
                    isDefault = p.IsDefault,
                    isValid = p.IsValid,
                    status = p.Status,
                    paperSizes = p.PaperSizes,
                    needsDriver = !p.IsValid
                }).ToList();
                return Result.success(data: new
                {
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                        .ToString().ToLowerInvariant(),
                    hasPrinter = items.Count > 0,
                    printerCount = items.Count,
                    defaultPrinter = _printers.GetDefault(),
                    needsAttention = items.Count == 0 || items.Any(x => x.needsDriver),
                    printers = items,
                    guidance = "打印机驱动由管理员通过打印机厂商官方渠道安装；客户端不自动下载或安装驱动。"
                }, cmd: "CheckPrinterDriver");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "CheckPrinterDriver");
            }
        }

        public string SetDefaultPrinter(string args)
        {
            var name = Str(ParseObject(args), "printer") ?? Str(ParseObject(args), "name");
            if (string.IsNullOrWhiteSpace(name)) return Result.error("缺少 printer 字段", cmd: "SetDefaultPrinter");
            var ok = _printers.SetDefault(name);
            return ok
                ? Result.success(data: new { primary = name }, cmd: "SetDefaultPrinter", msg: "默认打印机已更新")
                : Result.error("打印机不存在或切换失败", 400, "SetDefaultPrinter");
        }

        public string PrintTestPage(string args)
        {
            var name = Str(ParseObject(args), "printer");
            var ok = _printers.PrintTestPage(name);
            return ok
                ? Result.success(data: new { printer = name ?? _printers.GetDefault() },
                    cmd: "PrintTestPage", msg: "测试页已发送")
                : Result.error("测试页打印失败，请检查打印机是否可用", cmd: "PrintTestPage");
        }

        // ------------------------------------------------ 打印机匹配

        /// <summary>
        /// 三层打印机智能匹配（L1 文件类别 / L2 纸宽 / L3 历史偏好）。
        /// 参数：
        ///   target       文件路径或 URL（用扩展名识别类别，必填）
        ///   paperWidthMm 期望纸宽毫米（0=不过滤）
        ///   prefer       可选，强制指定偏好打印机名
        /// 返回候选列表（已按分数排序）+ best（首选）；无匹配时 best 为空。
        /// </summary>
        public string MatchPrinter(string args)
        {
            const string cmd = "MatchPrinter";
            try
            {
                var dt = ParseObject(args);
                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target))
                    return Result.error("缺少 target 字段（文件路径或 URL）", 400, cmd);
                var paperWidthMm = Math.Max(0, Int(dt, "paperWidthMm", 0));
                var prefer = Str(dt, "prefer");
                var category = PrinterMatcherService.Match(target, paperWidthMm, prefer);
                var best = category.Count > 0 ? category[0] : null;
                return Result.success(data: new
                {
                    target,
                    category = PrintFileTypes.CategoryOf(
                        ExtendedPrintService.NormalizeUrlSafe(target) != null
                            ? new Uri(ExtendedPrintService.NormalizeUrl(target)).AbsolutePath
                            : Path.GetExtension(target)),
                    paperWidthMm,
                    prefer,
                    candidates = category,
                    best = best != null
                        ? new { printer = best.Printer, driver = best.Driver, reason = best.Reason,
                              isDefault = best.IsDefault, isPreferred = best.IsPreferred }
                        : null,
                    matched = category.Count,
                    hint = category.Count == 0
                        ? "本机没有匹配的可用打印机，请确认驱动已安装"
                        : category.Count == 1
                            ? "已自动匹配唯一候选"
                            : "多台候选，best 为首选；调用方可让前端展示候选列表供用户选择"
                }, cmd: cmd);
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>
        /// 记录某文件类别的打印机偏好（用户手动选择后调用）。
        /// 参数：category（pdf/office/image/text/html/svg/xps/ofd/escpos）、printer（打印机名）。
        /// 偏好持久化到 data/preferences.json，下次匹配时自动优先该打印机。
        /// </summary>
        public string SetPrinterPreference(string args)
        {
            const string cmd = "SetPrinterPreference";
            try
            {
                var dt = ParseObject(args);
                var category = Str(dt, "category");
                var printer = Str(dt, "printer");
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(printer))
                    return Result.error("缺少 category 或 printer 字段", 400, cmd);
                PrinterMatcherService.RecordPreference(category, printer);
                return Result.success(data: new { category, printer, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                    cmd: cmd, msg: "打印机偏好已保存");
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>
        /// 只读：列出当前已记录的打印机偏好（各文件类别 → 首选打印机）。
        /// </summary>
        public string GetPrinterPreferences(string args)
        {
            const string cmd = "GetPrinterPreferences";
            try
            {
                var prefs = LoadAllPreferences();
                return Result.success(data: prefs, cmd: cmd);
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>从 data/preferences.json 读取全部偏好（供 GetPrinterPreferences 使用）。</summary>
        private static Dictionary<string, string> LoadAllPreferences()
        {
            var path = Path.Combine(SettingsStore.Instance.DataDir, "preferences.json");
            try
            {
                if (!System.IO.File.Exists(path)) return new Dictionary<string, string>();
                var json = System.IO.File.ReadAllText(path);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (obj["PreferredPrinterByCategory"] is Newtonsoft.Json.Linq.JObject jo)
                {
                    foreach (var kv in jo.Properties()) map[kv.Name] = kv.Value.ToString();
                }
                return map;
            }
            catch { return new Dictionary<string, string>(); }
        }

        // ------------------------------------------------------ PDF 驱动配置

        public string GetPdfDriverConfig(string args)
        {
            var targetFile = Path.Combine(SettingsStore.Instance.DriverDir, "SumatraPDF-settings.txt");
            var config = "";
            try
            {
                if (File.Exists(targetFile)) config = File.ReadAllText(targetFile);
            }
            catch (Exception ex) { Tools.Logger("读取驱动配置失败: " + ex.Message); }
            return Result.success(data: new { config, path = targetFile }, cmd: "GetPdfDriverConfig");
        }

        public string SetPdfPrintDriverConfig(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var config = Str(dt, "config");
                if (config == null) return Result.error("缺少 config 字段", cmd: "SetPdfPrintDriverConfig");
                var targetFile = Path.Combine(SettingsStore.Instance.DriverDir, "SumatraPDF-settings.txt");
                File.WriteAllText(targetFile, config);
                return Result.success(data: new { path = targetFile },
                    cmd: "SetPdfPrintDriverConfig", msg: "the config has updated");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "SetPdfPrintDriverConfig"); }
        }

        // ------------------------------------------------------------ 磁盘

        public string ScanDisk(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var age = Int(dt, "maxAgeDays", SettingsStore.Instance.Current.CleanMaxAgeDays);
                var minSize = Int(dt, "minSizeMB", SettingsStore.Instance.Current.CleanMinSizeMB);
                var items = _cleaner.Scan(age, minSize);
                return Result.success(data: new
                {
                    count = items.Count,
                    totalBytes = items.Sum(x => x.SizeBytes),
                    totalText = CleanCandidate.FormatSize(items.Sum(x => x.SizeBytes)),
                    items = items.Take(500).ToList(),
                    truncated = items.Count > 500
                }, cmd: "ScanDisk");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "ScanDisk"); }
        }

        /// <summary>
        /// 清理磁盘。默认 dryRun=true，必须显式传 dryRun:false 才会真正删除；
        /// 且只能删除 AllowedRoots 下、不在白名单内的文件。
        /// </summary>
        public string CleanDisk(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var paths = new List<string>();
                if (dt["paths"] is JArray arr)
                    paths.AddRange(arr.Select(x => x.ToString()));
                else
                {
                    var single = Str(dt, "path");
                    if (!string.IsNullOrWhiteSpace(single)) paths.Add(single);
                }
                var dryRun = Bool(dt, "dryRun", true);
                var recycle = Bool(dt, "useRecycleBin", SettingsStore.Instance.Current.UseRecycleBin);
                var result = _cleaner.Clean(paths, dryRun, recycle);
                return Result.success(data: result, cmd: "CleanDisk",
                    msg: dryRun ? "试运行完成，未删除任何文件" : "清理完成");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "CleanDisk"); }
        }

        /// <summary>
        /// 清理应用自产的临时文件（条码 PNG、ESC/POS dump、转换中间件等）。
        /// 只扫描应用 temp/ 目录，阈值可设为 0（不限大小）；默认 dryRun=true，
        /// 必须显式 dryRun:false 才真正删除；删除时默认走回收站。
        /// </summary>
        public string CleanAppTemp(string args)
        {
            const string cmd = "CleanAppTemp";
            try
            {
                var dt = ParseObject(args);
                var tempDir = SettingsStore.Instance.TempDir;
                var dryRun = Bool(dt, "dryRun", true);
                var recycle = Bool(dt, "useRecycleBin", SettingsStore.Instance.Current.UseRecycleBin);

                var cutoff = DateTime.Now;
                var maxAgeDays = Int(dt, "maxAgeDays", 0);
                if (maxAgeDays > 0) cutoff = DateTime.Now.AddDays(-maxAgeDays);
                // minSizeMB 允许 0（应用自产临时文件多为几 KB~几十 KB，默认阈值会全部漏掉）
                var minBytes = (long)Math.Max(0, Int(dt, "minSizeMB", 0)) * 1024 * 1024;

                var candidates = new List<CleanCandidate>();
                if (Directory.Exists(tempDir))
                {
                    foreach (var f in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (!fi.Exists || fi.Length < minBytes) continue;
                            if (fi.LastWriteTime > cutoff) continue;
                            candidates.Add(new CleanCandidate
                            {
                                Path = f,
                                SizeBytes = fi.Length,
                                LastWrite = fi.LastWriteTime,
                                Category = "应用临时文件"
                            });
                        }
                        catch { /* 单文件无权限跳过 */ }
                    }
                }
                candidates = candidates.OrderByDescending(x => x.SizeBytes).ToList();

                if (dryRun)
                {
                    return Result.success(data: new
                    {
                        dryRun = true,
                        count = candidates.Count,
                        totalBytes = candidates.Sum(x => x.SizeBytes),
                        totalText = CleanCandidate.FormatSize(candidates.Sum(x => x.SizeBytes)),
                        items = candidates.Take(200).ToList()
                    }, cmd: cmd, msg: "试运行完成，未删除任何文件");
                }

                // 真正删除：走回收站（可恢复）
                var result = _cleaner.Clean(
                    candidates.Select(x => x.Path), dryRun: false, useRecycleBin: recycle);
                return Result.success(data: new
                {
                    dryRun = false,
                    deleted = result.Deleted,
                    skipped = result.Skipped,
                    freedBytes = result.FreedBytes,
                    freedText = result.FreedText,
                    errors = result.Errors
                }, cmd: cmd, msg: "应用临时文件清理完成");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: cmd); }
        }

        public string GetDiskInfo(string args) =>
            Result.success(data: _cleaner.GetDiskInfo(), cmd: "GetDiskInfo");

        // ------------------------------------------------------------ 配置/状态

        public string GetSettings(string args) =>
            Result.success(data: SettingsStore.Instance.Current, cmd: "GetSettings");

        public string SaveSettings(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var next = JsonConvert.DeserializeObject<AppSettings>(
                    JsonConvert.SerializeObject(SettingsStore.Instance.Current));
                foreach (var prop in dt.Properties())
                {
                    var pi = typeof(AppSettings).GetProperty(prop.Name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null || !pi.CanWrite) continue;
                    var value = prop.Value.ToObject(pi.PropertyType);
                    pi.SetValue(next, value);
                }
                SettingsStore.Instance.Save(next);
                return Result.success(data: next, cmd: "SaveSettings",
                    msg: "配置已保存；端口/自启等改动需重启应用生效");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "SaveSettings"); }
        }

        public string GetStatus(string args)
        {
            var s = SettingsStore.Instance.Current;
            return Result.success(data: new
            {
                app = Brand.NameEn,
                appCn = Brand.NameCn,
                version = Brand.Version,
                language = Utils.L10n.Current,
                languageConfigured = Utils.L10n.Configured,
                languageDetected = Utils.L10n.Detected,
                framework = Environment.Version.ToString(),
                websocket = "ws://" + s.WebHost + ":" + s.WebPort + "/ws",
                webDocs = "http://" + s.WebHost + ":" + s.WebPort,
                aoEnabled = s.AoEnabled,
                aoOnline = AoClientService.Instance.Online,
                aoRelayUrl = s.AoRelayUrl,
                aoDeviceId = AoClientService.Instance.DeviceId,
                defaultPrinter = _printers.GetDefault(),
                supportedTypes = PrintFileTypes.All,
                queue = PrintQueueService.Instance.Summary(),
                uptimeSeconds = (long)(DateTime.Now - PrintQueueService.Instance.StartedAt).TotalSeconds
            }, cmd: "GetStatus");
        }

        /// <summary>本机指纹：跨浏览器/会话稳定，作为打印习惯的 machine-key。</summary>
        public string GetMachineFingerprint(string args)
        {
            var fp = Utils.MachineFingerprint.Get();
            return Result.success(data: new
            {
                machineId = fp,
                machineGuid = Utils.MachineFingerprint.MachineGuid(),
                volumeSerial = Utils.MachineFingerprint.VolumeSerial(),
                os = Environment.OSVersion.VersionString,
                user = Environment.UserName,
                app = Brand.NameEn,
                appCn = Brand.NameCn,
                version = Utils.Brand.Version
            }, cmd: "GetMachineFingerprint");
        }

        // ------------------------------------------------------------ 更新

        /// <summary>拉取 Gitee latest.json，比对当前版本，不实际下载。</summary>
        public string CheckUpdate(string args)
        {
            var info = new UpdateService().CheckUpdateAsync().GetAwaiter().GetResult();
            return Result.success(data: info, cmd: "CheckUpdate",
                msg: info.HasUpdate ? "发现新版本 " + info.Remote : "已是最新版本");
        }

        /// <summary>下载、校验 SHA256、替换当前目录。返回 Installed 标识与备份路径。</summary>
        public string ApplyUpdate(string args)
        {
            var info = new UpdateService().ApplyUpdateAsync().GetAwaiter().GetResult();
            return info.Installed
                ? Result.success(data: info, cmd: "ApplyUpdate", msg: "已更新；请重启应用")
                : Result.error(info.Error ?? "更新失败", 500, "ApplyUpdate", data: info);
        }

        // ------------------------------------------------------------ 辅助

        private static JObject ParseObject(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return new JObject();
            var token = JToken.Parse(args);
            if (token is JObject o) return o;
            throw new ArgumentException("请求体必须是 JSON 对象");
        }

        private static string Str(JObject o, string key)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? null : v.ToString();
        }

        private static int Int(JObject o, string key, int fallback)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? fallback : v.Value<int>();
        }

        private static bool Bool(JObject o, string key, bool fallback)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? fallback : v.Value<bool>();
        }

        /// <summary>
        /// 把内部实现标识收敛为文件类别，对外只暴露 data.engine 的类别值。
        /// 内部日志与诊断仍保留完整标识，便于排障；外部不承诺具体实现方式。
        /// </summary>
        private static string EngineCategory(string engine)
        {
            var e = engine ?? "";
            if (e.StartsWith("LibreOffice", StringComparison.OrdinalIgnoreCase)) return "office";
            if (e.StartsWith("OFD", StringComparison.OrdinalIgnoreCase)) return "ofd";
            if (e.StartsWith("XPS", StringComparison.OrdinalIgnoreCase)) return "xps";
            if (e.StartsWith("SVG", StringComparison.OrdinalIgnoreCase)) return "svg";
            if (e.StartsWith("ESC/POS", StringComparison.OrdinalIgnoreCase)) return "escpos";
            if (e.StartsWith("GDI+Text", StringComparison.OrdinalIgnoreCase)) return "text";
            if (e.StartsWith("GDI+", StringComparison.OrdinalIgnoreCase)) return "image";
            if (e.IndexOf("SumatraPDF", StringComparison.OrdinalIgnoreCase) >= 0) return "pdf";
            if (e.StartsWith("ShellVerb", StringComparison.OrdinalIgnoreCase)) return "shell";
            if (e.StartsWith("Copy", StringComparison.OrdinalIgnoreCase)) return "file";
            return string.IsNullOrWhiteSpace(e) ? "unknown" : e.ToLowerInvariant();
        }

        private static PrintJob BuildJob(JObject dt, string target)
        {
            var isHttp = ExtendedPrintService.NormalizeUrlSafe(target) != null;
            var s = SettingsStore.Instance.Current;
            // 用 HasKey 区分「未传」与「显式传空/null」：未传时回退设置页的默认打印参数；
            // 显式传空则尊重调用方（例如 paperKind:"" 表示跟随打印机，而非套用默认值）。
            var copiesReq = HasKey(dt, "copies");
            var duplexReq = HasKey(dt, "duplex");
            var orientationReq = HasKey(dt, "orientation") || HasKey(dt, "scale");
            var paperReq = HasKey(dt, "paperKind");
            var marginReq = HasKey(dt, "margin");
            var pageOrderReq = HasKey(dt, "pageOrder") || HasKey(dt, "parity");
            return new PrintJob
            {
                Source = isHttp ? "url" : "file",
                Target = target,
                Printer = Str(dt, "printer") ?? s.DefaultPrinter ?? "",
                Copies = copiesReq ? Math.Max(1, Int(dt, "copies", 1)) : Math.Max(1, s.DefaultCopies),
                Fit = Str(dt, "fit") ?? "fit",
                Scale = orientationReq
                    ? (Str(dt, "orientation") ?? Str(dt, "scale") ?? "portrait")
                    : (string.IsNullOrWhiteSpace(s.DefaultOrientation) ? "portrait" : s.DefaultOrientation),
                PaperKind = paperReq ? (Str(dt, "paperKind") ?? "") : (s.DefaultPaperSize ?? ""),
                Pages = Str(dt, "printPages") ?? Str(dt, "pages") ?? "1-",
                Duplex = duplexReq ? (Str(dt, "duplex") ?? "simplex")
                    : (string.IsNullOrWhiteSpace(s.DefaultDuplex) ? "simplex" : s.DefaultDuplex),
                Color = TriBool(dt, "color"),
                Collate = Bool(dt, "collate", true),
                Margin = marginReq ? Math.Max(0, Math.Min(500, Int(dt, "margin", 50)))
                    : Math.Max(0, Math.Min(500, s.DefaultMargin)),
                Bin = Str(dt, "bin") ?? "",
                Dpi = Math.Max(0, Int(dt, "dpi", 0)),
                PageOrder = pageOrderReq ? (Str(dt, "pageOrder") ?? Str(dt, "parity") ?? "")
                    : (s.DefaultPageOrder ?? "")
            };
        }

        /// <summary>字段是否由调用方显式提供（含显式 null/空）；用于区分「未传」与「传空」。</summary>
        private static bool HasKey(JObject o, string key) => o != null && o[key] != null;

        /// <summary>三态布尔：缺省 / auto 表示跟随打印机，不强行设置。</summary>
        private static bool? TriBool(JObject o, string key)
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return null;
            var s = v.ToString().Trim().ToLowerInvariant();
            if (s == "" || s == "auto" || s == "default") return null;
            if (s == "true" || s == "1" || s == "color" || s == "彩色") return true;
            if (s == "false" || s == "0" || s == "monochrome" || s == "mono" || s == "黑白") return false;
            return null;
        }
    }
}

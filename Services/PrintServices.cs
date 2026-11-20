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
                // 统一把 printer 字段（友好别名 / host:port / 本机打印机名）解析成明确通道
                var resolved = SlugResolver.Resolve(job.Printer);
                var outcome = resolved.Channel == "Net"
                    ? PrintNetwork(resolved.Printer, job)
                    : _extended.Print(job);
                // 网络直发通道记一条打印审计流水（本机工位机），成功/失败都记，供对账排障
                if (resolved.Channel == "Net")
                {
                    try { PrintEventStore.Record(resolved.Printer, outcome.Engine, 0, job.Copies,
                        outcome.Ok, outcome.Ok ? outcome.Message : (outcome.Detail ?? outcome.Message)); }
                    catch { /* 审计失败不影响主流程 */ }
                }
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "PrintFile", data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    path = target,
                    printer = job.Printer,
                    channel = resolved.Channel,
                    slug = resolved.FromSlug ? resolved.Slug : null,
                    resolve = resolved.Detail,
                    engine = EngineCategory(outcome.Engine),
                    message = outcome.Message,
                    supportedTypes = PrintFileTypes.All
                }, cmd: "PrintFile", msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "PrintFile");
            }
        }

        /// <summary>网络直发：把 job 渲染成 PDF 字节，交给 NetPrinterService 按 host:port 发（免驱动）。</summary>
        private PrintOutcome PrintNetwork(string host, PrintJob job)
        {
            var net = new NetPrinterService();
            var pdf = net.ToPdfBytes(job, _extended, out var renderedFrom);
            if (pdf == null)
                return new PrintOutcome
                {
                    Ok = false, Engine = "Net",
                    Message = "该文件类型无法渲染为 PDF，不能走网络直发：" + Path.GetExtension(job.Target),
                    Detail = "请改用已安装驱动的本机打印机，或先转成 PDF 再发"
                };
            return net.PrintSmart(host, pdf, job.Copies);
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
                // 透传批次/计划/幂等参数（可选）
                job.GroupId = Str(dt, "groupId") ?? Str(dt, "group") ?? "";
                var order = Int(dt, "groupOrder", -1);
                job.GroupOrder = order >= 0 ? order : 0;
                if (string.IsNullOrEmpty(job.GroupId)) job.GroupOrder = 0;
                if (!string.IsNullOrWhiteSpace(Str(dt, "dueAt")))
                {
                    // 支持 ISO8601（UTC 优先）/ Unix 秒 / 本地 yyyy-MM-dd HH:mm:ss
                    job.DueAt = ParseDueAt(Str(dt, "dueAt"));
                }
                job.IdempotencyKey = Str(dt, "idempotencyKey") ?? "";
                var queued = PrintQueueService.Instance.Enqueue(job);
                return Result.success(data: new
                {
                    jobId = queued.Id,
                    deduped = queued.Deduped,
                    dueAt = queued.DueAt,
                    groupId = queued.GroupId,
                    groupOrder = queued.GroupOrder,
                    job = queued
                }, cmd: "QueuePrint",
                    msg: queued.Deduped ? "幂等命中，未重复入队"
                        : (queued.DueAt != null ? "计划任务已暂存，到点自动入队打印" : "已加入打印队列"));
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

        /// <summary>
        /// 召回（可撤回）：撤下「已入队、尚未开始打印」的任务并记审计留痕，
        /// 供物流中心核对「这单为什么没打 / 被谁撤回」。
        /// 正在打印（printing）或已出纸的任务无法撤回——裸 RAW/IPP 网络直发一旦出纸即不可撤销，
        /// 只能靠审计回执追溯，此方法对其返回 false 并附提示。
        /// 参数：id（必填，jobId）、reason（可选，撤回原因）。
        /// </summary>
        public string RecallJob(string args)
        {
            const string cmd = "RecallJob";
            try
            {
                var dt = ParseObject(args);
                var id = Str(dt, "id") ?? Str(dt, "jobId");
                if (string.IsNullOrWhiteSpace(id))
                    return Result.error("缺少 id 字段（jobId）", 400, cmd);
                var reason = Str(dt, "reason") ?? "";
                var msg = PrintQueueService.Instance.Recall(id, reason);
                if (msg.StartsWith("任务已召回"))
                    return Result.success(data: new { id, reason }, cmd: cmd, msg);
                // 任务不存在 → 404；printing 无法撤回 → 409（提示走审计追溯）
                var code = msg.StartsWith("任务不存在") ? 404 : 409;
                return Result.error(msg, code, cmd);
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        // ------------------------------------------------ 排期 / 计划任务

        /// <summary>
        /// 排一个计划打印任务：到点自动入队打印（复用队列的幂等去重/批次/断网补打）。
        /// 参数：dueAt（ISO8601 带时区 / Unix 秒 / 本地时间字符串）、job（PrintJob 字段：target/printer/copies/…）、
        ///       idempotencyKey（可选，触发重复也不重打）、note（可选备注）。
        /// 返回 planId，后续可用 ListSchedulePlans/RemoveSchedulePlan 管理。
        /// </summary>
        public string SchedulePrint(string args)
        {
            const string cmd = "SchedulePrint";
            try
            {
                var dt = ParseObject(args);
                var dueRaw = Str(dt, "dueAt") ?? Str(dt, "due") ?? Str(dt, "at");
                if (string.IsNullOrWhiteSpace(dueRaw))
                    return Result.error("缺少 dueAt 字段（ISO8601 带时区 / Unix 秒 / 本地时间）", 400, cmd);
                DateTime? due;
                try { due = ParseDueAt(dueRaw); }
                catch (Exception ex) { return Result.error("dueAt 格式无法解析: " + ex.Message, 400, cmd); }
                if (due <= DateTime.Now.AddSeconds(1))
                    due = DateTime.Now.AddSeconds(1); // 至少 1s 后（本地时间，与调度器一致）

                var job = new PrintJob();
                if (dt["job"] is JObject jobObj)
                {
                    var j = BuildJob(jobObj, ResolveTarget(jobObj));
                    job = j;
                }
                else
                {
                    // 也支持平铺：把外层 target/printer/copies 等直接当 job 字段
                    var target = ResolveTarget(dt);
                    if (string.IsNullOrWhiteSpace(target))
                        return Result.error("缺少 job.target 或 target 字段（要打印的文件路径/URL）", 400, cmd);
                    job = BuildJob(dt, target);
                }
                job.IdempotencyKey = Str(dt, "idempotencyKey") ?? "";
                job.DueAt = due.Value; // 标记为计划任务

                var plan = new ScheduledPrintScheduler.Plan
                {
                    DueAt = due.Value,
                    Job = job,
                    IdempotencyKey = job.IdempotencyKey,
                    Note = Str(dt, "note") ?? ""
                };
                var saved = ScheduledPrintScheduler.Instance.Add(plan);
                return Result.success(data: new
                {
                    planId = saved.Id,
                    dueAt = saved.DueAt,
                    note = saved.Note,
                    job = saved.Job,
                    idempotencyKey = saved.IdempotencyKey
                }, cmd: cmd, msg: "计划任务已排，到点自动入队打印");
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>列出全部计划任务（含 pending/done/cancelled 状态与下次触发时间）。</summary>
        public string ListSchedulePlans(string args)
        {
            const string cmd = "ListSchedulePlans";
            try
            {
                var plans = ScheduledPrintScheduler.Instance.GetAll();
                return Result.success(data: new
                {
                    summary = ScheduledPrintScheduler.Instance.Summary(),
                    count = plans.Count,
                    plans
                }, cmd: cmd);
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>撤回某条尚未触发的计划任务。参数：planId。</summary>
        public string RemoveSchedulePlan(string args)
        {
            const string cmd = "RemoveSchedulePlan";
            try
            {
                var dt = ParseObject(args);
                var planId = Str(dt, "planId") ?? Str(dt, "id");
                if (string.IsNullOrWhiteSpace(planId))
                    return Result.error("缺少 planId 字段", 400, cmd);
                // now=true：提前触发（把 DueAt 置为当前，下一轮 tick 立即入队）；否则撤回
                if (Bool(dt, "now", false))
                {
                    var fired = ScheduledPrintScheduler.Instance.Now(planId);
                    return fired
                        ? Result.success(data: new { planId, now = true }, cmd: cmd, msg: "计划任务已提前触发")
                        : Result.error("计划任务不存在或已非 pending", 404, cmd);
                }
                var ok = ScheduledPrintScheduler.Instance.Remove(planId);
                return ok
                    ? Result.success(data: new { planId }, cmd: cmd, msg: "计划任务已撤回")
                    : Result.error("计划任务不存在或已触发", 404, cmd);
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>
        /// 批量打印（波次）：把一批任务作为同一 group 按序入队，复用队列的批次顺序/幂等/断网补打。
        /// 参数：jobs（数组，每项为 PrintJob 字段：target/printer/copies/…）、groupId（可选，缺省自动生成）、
        ///       idempotencyKey（可选前缀，按序加 :index）、dueAt（可选，整批同一到点时间）。
        /// </summary>
        public string PrintBatch(string args)
        {
            const string cmd = "PrintBatch";
            try
            {
                var dt = ParseObject(args);
                var arr = dt["jobs"] as JArray;
                if (arr == null || arr.Count == 0)
                    return Result.error("缺少 jobs 数组（至少 1 个打印任务）", 400, cmd);
                var groupId = Str(dt, "groupId") ?? ("batch:" + Guid.NewGuid().ToString("N"));
                var keyPrefix = Str(dt, "idempotencyKey") ?? "";
                DateTime? due = null;
                if (!string.IsNullOrWhiteSpace(Str(dt, "dueAt")))
                {
                    try { due = ParseDueAt(Str(dt, "dueAt")); }
                    catch (Exception ex) { return Result.error("dueAt 格式无法解析: " + ex.Message, 400, cmd); }
                }

                var jobIds = new List<string>();
                int deduped = 0, skipped = 0, order = 0;
                foreach (var token in arr)
                {
                    if (!(token is JObject jo)) { skipped++; continue; }
                    PrintJob job;
                    try { job = BuildJob(jo, ResolveTarget(jo)); }
                    catch { skipped++; continue; } // 缺 target 等无效项跳过，不阻断整批
                    job.GroupId = groupId;
                    job.GroupOrder = order;
                    if (!string.IsNullOrEmpty(keyPrefix)) job.IdempotencyKey = keyPrefix + ":" + order;
                    job.DueAt = due; // 整批同一到点（null=立即）
                    var queued = PrintQueueService.Instance.Enqueue(job);
                    jobIds.Add(queued.Id);
                    if (queued.Deduped) deduped++;
                    order++;
                }
                if (jobIds.Count == 0)
                    return Result.error("jobs 中没有有效打印任务（每项需含 target/path/label/url/file）", 400, cmd);

                try { PrintEventStore.Record("batch:" + groupId, "Batch", 0, jobIds.Count, true,
                    "批量入队 " + jobIds.Count + " 项，幂等命中 " + deduped + " 项，跳过无效 " + skipped + " 项"); }
                catch { /* 审计失败不影响入队 */ }

                return Result.success(data: new
                {
                    groupId,
                    count = jobIds.Count,
                    deduped,
                    skipped,
                    dueAt = due,
                    jobIds
                }, cmd: cmd, msg: "批量任务已入队 " + jobIds.Count + " 项" +
                    (deduped > 0 ? "（幂等命中 " + deduped + " 项未重复入队）" : ""));
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>
        /// 智能推荐排期：基于本机近 14 天真实打印审计流水，按「一天中的小时」统计负载，
        /// 为一批任务推荐低峰错峰时段；无历史数据时退回默认均匀分布策略并标注 basedOn。
        /// 参数：count（任务数 1-200，默认 1）、printer（可选，仅统计某 host）、
        ///       withinHours（未来多少小时内，默认 8）、startAt（可选起始时间，默认当前）。
        /// </summary>
        public string RecommendPlan(string args)
        {
            const string cmd = "RecommendPlan";
            try
            {
                var dt = ParseObject(args);
                int count = Math.Max(1, Math.Min(200, Int(dt, "count", 1)));
                int withinHours = Math.Max(1, Math.Min(72, Int(dt, "withinHours", 8)));
                var host = Str(dt, "printer") ?? "";
                var start = string.IsNullOrWhiteSpace(Str(dt, "startAt")) ? DateTime.Now : ParseDueAt(Str(dt, "startAt"));
                if (start < DateTime.Now) start = DateTime.Now;

                var fromTs = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
                var history = PrintEventStore.Query(host, fromTs, 0, true, 1000);
                var hourLoad = new int[24];
                foreach (var e in history)
                {
                    var hour = DateTimeOffset.FromUnixTimeSeconds(e.Ts).ToLocalTime().Hour;
                    hourLoad[hour] += Math.Max(1, e.Copies);
                }
                bool hasHistory = history.Count > 0;

                var candidates = new List<(DateTime At, int Load)>();
                for (int i = 0; i < withinHours; i++)
                {
                    var t = start.AddHours(i + 1);
                    t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Local);
                    candidates.Add((t, hourLoad[t.Hour]));
                }
                var picked = new HashSet<DateTime>(candidates.OrderBy(x => x.Load).Take(count).Select(x => x.At));
                var slots = candidates.Select(x => new
                {
                    at = x.At,
                    hourLoad = x.Load,
                    recommended = picked.Contains(x.At)
                }).ToList();

                return Result.success(data: new
                {
                    basedOn = hasHistory ? "history" : "default",
                    historyDays = hasHistory ? 14 : 0,
                    sampleCount = history.Count,
                    recommendedCount = Math.Min(count, candidates.Count),
                    slots
                }, cmd: cmd, msg: hasHistory
                    ? "已按近 14 天打印低峰推荐错峰时段"
                    : "无历史数据，已按默认策略推荐均匀分布时段");
            }
            catch (Exception ex) { return Result.error(ex.Message, 500, cmd); }
        }

        /// <summary>
        /// 把 dueAt（ISO8601 / Unix 秒 / 本地日期时间）统一解析为「本地时间」DateTime。
        /// 关键：本项目所有队列/调度时间字段（EnqueuedAt/DueAt/FiredAt）都用 DateTime.Now 本地时区，
        /// 因此这里必须在任何分支都返回本地时间，不能混返 UTC，否则调度器比较会差 8 小时。
        /// </summary>
        private static DateTime ParseDueAt(string raw)
        {
            raw = raw.Trim();
            // Unix 秒 / 毫秒（含小数秒）→ UTC → 本地
            if (raw.Length >= 9 && raw.Length <= 16 && raw.All(c => char.IsDigit(c) || c == '.'))
            {
                var ts = long.Parse(raw.Split('.')[0]);
                var utc = ts < 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                    : DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
                return utc.ToLocalTime();
            }
            // 带明确时区（Z / ±HH:mm）的 ISO8601 → 转本地；无时区则按本机时区解释
            if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dto))
                return dto.UtcDateTime.ToLocalTime();
            // 兜底：本地日期时间字符串
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                return DateTime.SpecifyKind(parsed, DateTimeKind.Local);
            throw new FormatException("无法解析 dueAt: " + raw);
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
        /// 只读：扫描局域网打印机，叠加 WMI 已安装网络打印机 + SSDP/WSD + mDNS(IPP/IPPS/raw)
        /// + active（按指定网段 TCP 端口探测，须显式声明 methods 含 active 且给出 subnets）。
        /// 只发标准发现/握手报文、不发送打印数据、不装驱动、不改系统。
        /// 参数 timeoutMs(500-8000)、methods(installed,ssdp,mdns,active)、subnets、maxHosts(1-4096)。
        /// 结果仅用于识别设备；实际使用仍需 Windows 添加网络打印机并安装厂商驱动。
        /// </summary>
        public string ScanLanPrinters(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var timeout = dt["timeoutMs"]?.Value<int>() ?? 1500;
                var maxHosts = dt["maxHosts"]?.Value<int>() ?? 512;
                var subnets = dt["subnets"]?.ToString() ?? "";
                var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var methodsToken = dt["methods"];
                if (methodsToken is JArray arr && arr.Count > 0)
                    foreach (var m in arr) methods.Add(m.ToString());
                else if (methodsToken != null && !string.IsNullOrWhiteSpace(methodsToken.ToString()))
                    foreach (var m in methodsToken.ToString().Split(','))
                        if (!string.IsNullOrWhiteSpace(m)) methods.Add(m.Trim());

                var result = new LanPrinterScanner().Scan(timeout, methods, subnets, maxHosts);
                return Result.success(data: result, cmd: "ScanLanPrinters");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "ScanLanPrinters");
            }
        }

        /// <summary>
        /// 网络打印机智能直发：给定目标 IP（ip，可带端口）与文件（target），本机不装驱动即可打印。
        /// 文件先渲染为 PDF，再按缓存/自动探测选通道：631 IPP → 9100 RAW 降级；识别结果按 IP 缓存。
        /// RAW「连接成功即视为可达」（9100 无回执）；IPP 有 Job 回执。都不可达时明示需装驱动。
        /// 参数：ip（必填，host 或 host:port）、target（文件路径/URL）、copies。
        /// </summary>
        public string PrintToIp(string args)
        {
            const string cmd = "PrintToIp";
            try
            {
                var dt = ParseObject(args);
                var ip = Str(dt, "ip") ?? Str(dt, "printer") ?? "";
                if (string.IsNullOrWhiteSpace(ip))
                    return Result.error("缺少 ip 字段（目标打印机 IP，可带端口如 192.168.1.10:631）", 400, cmd);

                var target = ResolveTarget(dt);
                if (string.IsNullOrWhiteSpace(target))
                    return Result.error("缺少 target 字段（要打印的文件路径或 URL）", 400, cmd);
                var copies = Math.Max(1, Int(dt, "copies", 1));

                // 构造 job，复用虚拟打印管线把文件渲染成 PDF 字节
                var job = BuildJob(dt, target);
                job.Printer = ""; // 网络直发不占本机打印机名
                var bytes = new NetPrinterService().ToPdfBytes(job, _extended, out var renderedFrom);
                if (bytes == null)
                {
                    // 渲染失败也记一条审计（ok=false，engine=Render），便于排障「哪台打没打出去」
                    try { PrintEventStore.Record(ip, "Render", 0, copies, false,
                        "无法渲染为 PDF：" + Path.GetExtension(target)); } catch { }
                    return Result.error("该文件类型无法渲染为 PDF，不能网络直发：" +
                        Path.GetExtension(target), 400, cmd,
                        data: new { hint = "请改用已安装驱动的本机打印机，或先转成 PDF 再发" });
                }

                var outcome = new NetPrinterService().PrintSmart(ip, bytes, copies);
                // 打印审计：无论成败都记一条本机直发流水（host/engine/bytes/copies/ok/detail），供对账排障
                try { PrintEventStore.Record(ip, outcome.Engine, bytes.Length, copies, outcome.Ok,
                    outcome.Ok ? outcome.Message : (outcome.Detail ?? outcome.Message)); } catch { /* 审计失败不影响主流程 */ }

                if (!outcome.Ok)
                    return Result.error(outcome.Message, 502, cmd,
                        data: new { ip, engine = outcome.Engine, detail = outcome.Detail });

                return Result.success(data: new
                {
                    ip,
                    engine = outcome.Engine,
                    bytes = bytes.Length,
                    renderedFrom = renderedFrom,
                    message = outcome.Message,
                    detail = outcome.Detail
                }, cmd: cmd, msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, 502, cmd);
            }
        }

        /// <summary>
        /// 别名（slug）管理：把复杂目标 + 协议藏到一个友好名称后面，供打印入口统一智能调度。
        /// 纯本地 JSON 配置，不碰系统、不注册 Windows 打印机、不装驱动。
        /// 参数：slug（必填，友好名）；target（host:port / 裸 IP / 本机打印机驱动名 / 留空走默认）；
        ///       mode（可选 auto/ipp/raw/driver）；note（可选备注）。
        /// </summary>
        public string AddPrinterSlug(string args)
        {
            const string cmd = "AddPrinterSlug";
            try
            {
                var dt = ParseObject(args);
                var slug = Str(dt, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                    return Result.error("缺少 slug 字段（别名本体，如「前台打印机」）", 400, cmd);
                var target = Str(dt, "target");
                var mode = Str(dt, "mode") ?? "auto";
                var note = Str(dt, "note");

                var entry = new PrinterSlugEntry
                {
                    Slug = slug.Trim(),
                    Target = target ?? "",
                    Mode = mode ?? "auto",
                    Note = note ?? ""
                };
                var saved = PrinterSlugStore.Upsert(entry);
                return Result.success(data: new { slug = saved.Slug, target = saved.Target, mode = saved.Mode, note = saved.Note },
                    cmd: cmd, msg: "别名已保存");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        /// <summary>删除某条别名。参数：slug。</summary>
        public string RemovePrinterSlug(string args)
        {
            const string cmd = "RemovePrinterSlug";
            try
            {
                var dt = ParseObject(args);
                var slug = Str(dt, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                    return Result.error("缺少 slug 字段", 400, cmd);
                var ok = PrinterSlugStore.Remove(slug);
                if (!ok)
                    return Result.error("该别名不存在: " + slug, 404, cmd);
                return Result.success(data: new { slug }, cmd: cmd, msg: "别名已删除");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        /// <summary>
        /// 列出全部别名（供对接方查看有哪些「打印机名」可用，以及别名 → 目标的映射）。
        /// 可选按 slug 精确查一条：参数 slug 命中则只返回它；否则返回全部。
        /// </summary>
        public string ListPrintSlugs(string args)
        {
            const string cmd = "ListPrintSlugs";
            try
            {
                var dt = ParseObject(args);
                var one = Str(dt, "slug");
                if (!string.IsNullOrWhiteSpace(one))
                {
                    var e = PrinterSlugStore.Get(one);
                    if (e == null)
                        return Result.error("该别名不存在: " + one, 404, cmd);
                    return Result.success(data: new { list = new[] { e } }, cmd: cmd);
                }
                var all = PrinterSlugStore.List();
                return Result.success(data: new { count = all.Count, list = all }, cmd: cmd);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        // ------------------------------------------------------------ 打印审计（SQLite）

        /// <summary>
        /// 打印审计查询：按 host（设备/工位）/ 时间范围 / 成功与否检索直发流水，供运营对账与排障。
        /// 参数：host（可选，按设备过滤）、fromTs / toTs（Unix 秒，可选）、ok（true/false，可选）、limit（默认 100，最大 1000）。
        /// 仅查本机工位机记录的流水，不上传、不外发。
        /// </summary>
        public string ListPrintEvents(string args)
        {
            const string cmd = "ListPrintEvents";
            try
            {
                var dt = ParseObject(args);
                var host = Str(dt, "host");
                long fromTs = 0, toTs = 0;
                if (dt["fromTs"] != null) fromTs = dt["fromTs"].Value<long>();
                if (dt["toTs"] != null) toTs = dt["toTs"].Value<long>();
                bool? ok = dt["ok"] != null ? (bool?)dt["ok"] : (bool?)null;
                var limit = Math.Max(1, Math.Min(1000, Int(dt, "limit", 100)));

                var rows = PrintEventStore.Query(host, fromTs, toTs, ok, limit);
                return Result.success(data: new { count = rows.Count, events = rows }, cmd: cmd);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
            }
        }

        /// <summary>
        /// 归档打印流水：删除超过 keepDays 的旧事件，控制 SQLite 体积（大型物流中心长期运行必需）。
        /// 参数：keepDays（默认 30，0 表示清空全部；1-365 之间）。返回删除条数。
        /// </summary>
        public string ArchivePrintEvents(string args)
        {
            const string cmd = "ArchivePrintEvents";
            try
            {
                var dt = ParseObject(args);
                var keepDays = Math.Max(0, Math.Min(365, Int(dt, "keepDays", 30)));
                var deleted = PrintEventStore.Archive(keepDays);
                return Result.success(data: new { keepDays, deleted }, cmd: cmd, msg: "归档完成");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: cmd);
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

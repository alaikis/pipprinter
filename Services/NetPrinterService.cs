// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using DuoAssistant.Models;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 网络打印机直发通道：printer 目标形如 host:port（或 tcp://host:port）时，
    /// 把文件统一渲染成 PDF 字节，再按端口分流发送——不依赖 Windows 驱动、不装第三方驱动。
    ///   · 端口 631/636 → IPP / IPP-over-HTTPS 的 Print-Job（application/pdf 附件）
    ///   · 端口 9100/515 等 → RAW 直发 PDF 字节流（多数网络打印机 9100 口直接收 PDF）
    /// 发现阶段（LanPrinterScanner）只读不发包；只有显式下发打印时才通过本通道发包。
    /// </summary>
    public class NetPrinterService
    {
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>
        /// 智能直发：给定 host（可带端口），按缓存的识别结果选通道，未识别则自动探测
        /// 631/IPP → 9100/RAW 降级，记住结果写缓存。RAW「连上就算」，IPP 有回执。
        /// </summary>
        public PrintOutcome PrintSmart(string host, byte[] pdfBytes, int copies = 1, int timeoutMs = 15000)
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
                return new PrintOutcome { Ok = false, Message = "没有可发送的 PDF 数据" };

            // 归一化 host：解析出 host 与可选端口
            var h = (host ?? "").Trim();
            if (h.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) h = h.Substring(6);
            int explicitPort = 0;
            var hostOnly = h;
            var ci = h.LastIndexOf(':');
            if (ci > 0 && int.TryParse(h.Substring(ci + 1), out explicitPort))
                hostOnly = h.Substring(0, ci);

            // 缓存按 host 存
            var cached = Cache.Get(hostOnly);
            if (cached != null && cached.Mode != "driver")
            {
                var port = cached.Port > 0 ? cached.Port : 631;
                return cached.Mode == "raw" ? PrintRaw(hostOnly, port, pdfBytes, copies, timeoutMs)
                                             : PrintIpp(hostOnly, port, pdfBytes, copies, timeoutMs);
            }

            // 缓存为 driver：明示需驱动
            if (cached != null && cached.Mode == "driver")
                return new PrintOutcome
                {
                    Ok = false, Engine = "Net",
                    Message = "该设备此前识别为需驱动（IPP/RAW 均不可达）。请在 Windows 中安装厂商驱动后按名称打印。",
                    Detail = "可删除缓存重新识别：清除 数据目录/netprint-cache.json 中该 IP 条目"
                };

            // 未缓存：若显式给了端口就按该端口发；否则按 631 → 9100 自动降级探测
            if (explicitPort > 0)
            {
                var portOutcome = explicitPort == 631 || explicitPort == 636
                    ? PrintIpp(hostOnly, explicitPort, pdfBytes, copies, timeoutMs)
                    : PrintRaw(hostOnly, explicitPort, pdfBytes, copies, timeoutMs);
                if (portOutcome.Ok)
                    Cache.Put(hostOnly, explicitPort == 631 || explicitPort == 636 ? "ipp" : "raw", explicitPort);
                return portOutcome;
            }

            var ipp = PrintIpp(hostOnly, 631, pdfBytes, copies, timeoutMs);
            if (ipp.Ok)
            {
                Cache.Put(hostOnly, "ipp", 631);
                return ipp;
            }
            var raw = PrintRaw(hostOnly, 9100, pdfBytes, copies, timeoutMs);
            if (raw.Ok)
            {
                Cache.Put(hostOnly, "raw", 9100);
                raw.Detail = "已自动识别为 9100 RAW 通道（连接成功即视为可达；9100 无回执，请留意是否出纸）";
                return raw;
            }

            Cache.Put(hostOnly, "driver", 0);
            return new PrintOutcome
            {
                Ok = false, Engine = "Net",
                Message = $"无法直发 {hostOnly}：IPP(631) 与 RAW(9100) 均不可达",
                Detail = "该设备可能未开放网络打印口，或只接受厂商私有协议。请在 Windows 中安装官方驱动后按名称打印；或确认设备已开启 IPP/RAW。"
            };
        }

        /// <summary>目标是否为 host:port 形式的网络打印机。</summary>
        public static bool IsNetworkTarget(string printer)
            => !string.IsNullOrWhiteSpace(printer) && EscPosService.TryParseNetwork(printer, out _, out _);

        /// <summary>
        /// 把本地文件渲染成 PDF 字节：PDF 直接读；其余支持类型复用虚拟打印管线
        /// （ExtendedPrintService.PrintVirtual → "Microsoft Print to PDF"）输出到 temp 再读字节。
        /// 返回 null 表示该类型无法渲染为 PDF（网络直发不支持）。
        /// </summary>
        public byte[] ToPdfBytes(PrintJob job, ExtendedPrintService extSvc, out string renderedFrom)
        {
            renderedFrom = "";
            var ext = Path.GetExtension(job.Target).ToLowerInvariant();

            // PDF 无需转换，直接读
            if (Array.IndexOf(PrintFileTypes.Pdf, ext) >= 0 && File.Exists(job.Target))
            {
                renderedFrom = job.Target;
                try { return File.ReadAllBytes(job.Target); }
                catch { return null; }
            }

            // 其余类型：走虚拟打印输出 PDF 文件
            var outPath = Path.Combine(SettingsStore.Instance.TempDir,
                "netprint_" + Guid.NewGuid().ToString("N") + ".pdf");
            var outcome = extSvc.PrintVirtual(job, NativePrintService.PdfVirtualPrinter, outPath);
            if (!outcome.Ok || !File.Exists(outPath)) return null;

            renderedFrom = outPath;
            try { return File.ReadAllBytes(outPath); }
            finally { try { File.Delete(outPath); } catch { } }
        }

        /// <summary>主入口：按端口把 PDF 字节发到网络打印机。</summary>
        public PrintOutcome Print(string printer, byte[] pdfBytes, int copies, int timeoutMs = 15000)
        {
            if (!EscPosService.TryParseNetwork(printer, out var host, out var port))
                return new PrintOutcome { Ok = false, Message = "目标不是 host:port 形式的网络打印机" };
            if (pdfBytes == null || pdfBytes.Length == 0)
                return new PrintOutcome { Ok = false, Message = "没有可发送的 PDF 数据" };

            copies = Math.Max(1, copies);

            if (port == 631 || port == 636)
            {
                // 636 走 https，631 走 http；多数打印机只用 631
                return PrintIpp(host, port, pdfBytes, copies, timeoutMs);
            }

            // 9100 / 515 / 其它 → RAW 直发
            return PrintRaw(host, port, pdfBytes, copies, timeoutMs);
        }

        /// <summary>RAW 直发：连接 host:port，写 PDF 字节流。PDF 不支持客户端拼接多份，发 1 份。</summary>
        private PrintOutcome PrintRaw(string host, int port, byte[] data, int copies, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    if (!connect.Wait(timeoutMs))
                        return new PrintOutcome { Ok = false, Message = $"连接网络打印机超时 {host}:{port}" };
                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = timeoutMs;
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                    }
                }
                var note = copies > 1 ? "；多份请在打印机端设置，RAW 直发仅送 1 份" : "";
                return new PrintOutcome
                {
                    Ok = true,
                    Engine = "RAW/TCP",
                    Message = $"已发送到网络打印机 {host}:{port}（{data.Length} 字节）{note}"
                };
            }
            catch (Exception ex)
            {
                return new PrintOutcome { Ok = false, Message = "网络打印机 RAW 发送失败: " + ex.Message, Detail = ex.ToString() };
            }
        }

        /// <summary>IPP Print-Job：HTTP/HTTPS POST 到 /ipp 或 /printers/ipp，application/pdf 附件。</summary>
        private PrintOutcome PrintIpp(string host, int port, byte[] data, int copies, int timeoutMs)
        {
            var scheme = port == 636 ? "https" : "http";
            // 常见 IPP 端点：/ipp、/printers/ipp、/ipp/print；逐个尝试
            var endpoints = new[] { "/ipp/print", "/ipp", "/printers/ipp", "/" };
            var lastErr = "";
            foreach (var ep in endpoints)
            {
                try
                {
                    var req = _http.PostAsync($"{scheme}://{host}:{port}{ep}",
                        new ByteArrayContent(data), CancellationToken.None);
                    var resp = req.GetAwaiter().GetResult();
                    var body = resp.StatusCode == HttpStatusCode.OK ? "" : resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Accepted)
                        return new PrintOutcome
                        {
                            Ok = true,
                            Engine = "IPP",
                            Message = $"已通过 IPP 发送到 {host}:{port}{ep}（{copies} 份，{data.Length} 字节）",
                            Detail = "Print-Job 已提交；多份请留意打印机是否重复出纸"
                        };
                    lastErr = $"HTTP {(int)resp.StatusCode} {ep} {body}";
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }
            return new PrintOutcome
            {
                Ok = false,
                Engine = "IPP",
                Message = $"IPP 发送失败 {host}:{port}：{lastErr}",
                Detail = "该设备可能未开启 IPP，或端口非 631。可改用 9100 RAW 直发，或在 Windows 中安装厂商驱动后按名称打印。"
            };
        }
        /// <summary>
        /// 网络打印机识别结果缓存：按 host 记住走 ipp / raw / driver，避免每次重复探测。
        /// 持久化到 数据目录/netprint-cache.json。RAW「连上就算」是探测结论，非打印回执。
        /// </summary>
        public static class Cache
        {
            private static readonly object Gate = new object();
            private static Dictionary<string, Entry> _map;
            private static string Path => System.IO.Path.Combine(SettingsStore.Instance.DataDir, "netprint-cache.json");

            public class Entry
            {
                public string Mode { get; set; } = "";
                public int Port { get; set; }
                public long Ts { get; set; }
            }

            private static Dictionary<string, Entry> Map()
            {
                if (_map != null) return _map;
                try
                {
                    var json = File.Exists(Path) ? File.ReadAllText(Path) : "{}";
                    _map = Newtonsoft.Json.Linq.JObject.Parse(json)
                        .Properties().ToDictionary(p => p.Name,
                            p => new Entry
                            {
                                Mode = p.Value["mode"]?.ToString() ?? "",
                                Port = (int?)p.Value["port"] ?? 0,
                                Ts = (long?)p.Value["ts"] ?? 0
                            },
                            System.StringComparer.OrdinalIgnoreCase);
                }
                catch { _map = new Dictionary<string, Entry>(System.StringComparer.OrdinalIgnoreCase); }
                return _map;
            }

            public static Entry Get(string host)
            {
                lock (Gate)
                {
                    var m = Map();
                    m.TryGetValue(host, out var e);
                    return e;
                }
            }

            public static void Put(string host, string mode, int port)
            {
                lock (Gate)
                {
                    var m = Map();
                    m[host] = new Entry { Mode = mode, Port = port, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    try
                    {
                        var json = new Newtonsoft.Json.Linq.JObject();
                        foreach (var kv in m)
                            json[kv.Key] = new Newtonsoft.Json.Linq.JObject
                            { ["mode"] = kv.Value.Mode, ["port"] = kv.Value.Port, ["ts"] = kv.Value.Ts };
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                        File.WriteAllText(Path, json.ToString());
                    }
                    catch (Exception ex) { Tools.Logger("网络打印缓存写入失败: " + ex.Message); }
                }
            }
        }
    }
}

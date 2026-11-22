// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 内嵌 ASP.NET Core (Kestrel) Web 服务：托管 docs/web 静态文档站 + REST 命令端点
    /// + 原生 WebSocket（/ws，与 HTTP 同端口）。与托盘进程同生命周期，端口由 appsettings.json 配置。
    /// </summary>
    public sealed class WebServerService
    {
        private WebApplication _app;
        private readonly WsHub _ws = new WsHub();

        public string Endpoint { get; }
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        /// <summary>WebSocket 端点（与 HTTP 共用主机/端口）。</summary>
        public string WebSocketEndpoint
        {
            get
            {
                var s = SettingsStore.Instance.Current;
                var scheme = s.WebHost.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
                return scheme + "://" + s.WebHost + ":" + s.WebPort + "/ws";
            }
        }

        public int WsConnectionCount => _ws.Count;

        public WebServerService()
        {
            var s = SettingsStore.Instance.Current;
            Endpoint = "http://" + s.WebHost + ":" + s.WebPort;
        }

        public async Task StartAsync()
        {
            var docs = SettingsStore.Instance.DocsDir;
            if (!Directory.Exists(docs))
            {
                Directory.CreateDirectory(docs);
                File.WriteAllText(Path.Combine(docs, "index.html"),
                    "<!doctype html><meta charset='utf-8'><h1>" + Brand.Title +
                    "</h1><p>文档目录缺失，已自动创建。</p>");
            }

            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ApplicationName = "PipPrinter.Docs",
                    ContentRootPath = AppDomain.CurrentDomain.BaseDirectory,
                    EnvironmentName = Environments.Production
                });
                builder.WebHost.UseUrls(Endpoint);
                builder.Logging.ClearProviders(); // 不把 Kestrel 日志灌进控制台/日志目录

                builder.Services.AddCors(options =>
                    options.AddPolicy("local", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

                var app = builder.Build();
                app.UseCors("local");
                // 原生 WebSocket：/ws 与文档/REST 共用同一端口，支持 TLS 自动终结
                app.UseWebSockets();

                var fsOptions = new FileServerOptions
                {
                    FileProvider = new PhysicalFileProvider(docs),
                    EnableDefaultFiles = true,
                    EnableDirectoryBrowsing = false
                };
                fsOptions.DefaultFilesOptions.DefaultFileNames.Clear();
                fsOptions.DefaultFilesOptions.DefaultFileNames.Add("index.html");
                app.UseFileServer(fsOptions);

                MapApi(app, docs);
                _app = app;
                Tools.Logger("WebSocket 端点已挂载: " + WebSocketEndpoint);
                await app.StartAsync().ConfigureAwait(false);
                IsRunning = true;
                LastError = null;
                Tools.Logger("Web 文档服务已启动: " + Endpoint);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = ex.Message;
                Tools.Logger("Web 文档服务启动失败: " + ex);
            }
        }

        /// <summary>命令返回的是已经序列化好的 Result JSON，直接透传避免二次序列化。</summary>
        private static IResult Json(string json) => Results.Content(json, "application/json; charset=utf-8");

        /// <summary>向所有 WebSocket 连接广播消息（队列变更通知等）。</summary>
        public void Broadcast(string payload) => _ws.Broadcast(payload);

        private void MapApi(WebApplication app, string docs)
        {
            // WebSocket 升级端点（与 HTTP 同端口）
            app.Map("/ws", async (HttpContext ctx) => await _ws.Handle(ctx));

            // 轻量探活：供负载均衡器 / 监控脚本秒级判断服务是否存活
            app.MapGet("/health", () => Results.Json(new
            {
                status = "ok",
                app = Brand.NameEn,
                version = Brand.Version,
                uptimeSeconds = (long)(DateTime.Now - PrintQueueService.Instance.StartedAt).TotalSeconds,
                web = Program.WebRunning,
                ws = Program.WsRunning,
                wsConnections = Program.WebWsConnections,
                aoOnline = AoClientService.Instance.Online,
                queue = PrintQueueService.Instance.Summary()
            }));

            app.MapGet("/api/status", () => Json(CommandDispatcher.Execute("GetStatus", "{}")));
            app.MapGet("/api/commands", () => Results.Json(new
            {
                code = 200,
                data = CommandDispatcher.ListCommands()
            }));
            app.MapGet("/api/printers", () => Json(CommandDispatcher.Execute("GetPrinterList", "{}")));
            // 只读：局域网打印机扫描（WMI 已安装网络打印机 + SSDP/WSD + mDNS + 可选 active 网段探测）。
            // ?timeoutMs=1500&methods=installed,ssdp,mdns,active&subnets=192.168.1.0/24&maxHosts=512
            // active 须显式声明且给出 subnets 才执行；只握手探测端口、不发送打印数据、不装驱动、不改系统。
            app.MapGet("/api/printers/scan", async (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var timeout = int.TryParse(q["timeoutMs"], out var t) ? t : 1500;
                var maxHosts = int.TryParse(q["maxHosts"], out var mh) ? mh : 512;
                var methods = q["methods"].ToString();
                var subnets = q["subnets"].ToString();
                var body = new JObject
                {
                    ["timeoutMs"] = timeout,
                    ["methods"] = methods,
                    ["subnets"] = subnets,
                    ["maxHosts"] = maxHosts
                };
                return Json(CommandDispatcher.Execute("ScanLanPrinters", body.ToString()));
            });
            app.MapGet("/api/queue", () => Json(CommandDispatcher.Execute("GetQueue", "{}")));
            app.MapGet("/api/disk", () => Json(CommandDispatcher.Execute("GetDiskInfo", "{}")));
            app.MapGet("/api/machine", () => Json(CommandDispatcher.Execute("GetMachineFingerprint", "{}")));
            app.MapGet("/api/update/check", () => Json(CommandDispatcher.Execute("CheckUpdate", "{}")));
            app.MapPost("/api/update/apply", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("ApplyUpdate", body));
            });
            app.MapGet("/api/docs", () => Results.Json(new
            {
                code = 200,
                data = Directory.GetFiles(docs, "*.html")
                    .Select(Path.GetFileName).OrderBy(x => x).ToList()
            }));

            app.MapPost("/api/command", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (!CommandDispatcher.IsValidJson(body))
                    return Results.Json(new { code = 400, error = "请求不是合法 JSON" });
                var cmd = JObject.Parse(body)["cmd"]?.ToString();
                // 不指定 cmd 时默认 auto：自动识别打印内容与文件形式
                if (string.IsNullOrWhiteSpace(cmd)) cmd = "AutoPrint";
                else if (cmd.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
                         cmd.Equals("auto", StringComparison.OrdinalIgnoreCase)) cmd = "AutoPrint";
                return Json(CommandDispatcher.Execute(cmd, body));
            });

            // 便捷端点：直接提交打印任务
            app.MapPost("/api/print", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                return Json(CommandDispatcher.Execute("QueuePrint", body));
            });

            // 便捷端点：生成条码/二维码并直接打印
            app.MapPost("/api/barcode", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintBarcode", body));
            });

            // 虚拟打印：输出 PDF / XPS 文件
            app.MapPost("/api/print/topdf", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintToPdf", body));
            });
            app.MapPost("/api/print/toxps", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintToXps", body));
            });

            // ESC/POS 热敏小票原始打印
            app.MapPost("/api/escpos", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintEscPos", body));
            });

            // 网络打印机智能直发：body {ip, target, copies}；本机不装驱动，自动选 631/IPP 或 9100/RAW
            app.MapPost("/api/print-to-ip", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintToIp", body));
            });

            // 打印机别名（slug）管理：GET 列出 / POST 新增或覆盖 / DELETE 删除。
            // 别名把 host:port 等复杂目标藏在一个友好名称后，打印入口统一智能调度。
            app.MapGet("/api/print-slugs", () => Json(CommandDispatcher.Execute("ListPrintSlugs", "{}")));
            app.MapPost("/api/print-slugs", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("AddPrinterSlug", body));
            });
            app.MapDelete("/api/print-slugs", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("RemovePrinterSlug", body));
            });

            // 打印审计：GET 查询本机直发/打印流水（?host=&fromTs=&toTs=&ok=&limit=）；
            // POST /archive 归档旧流水（body {keepDays}，0=清空）。
            app.MapGet("/api/print-events", async (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var body = new JObject
                {
                    ["host"] = q["host"].ToString(),
                    ["fromTs"] = long.TryParse(q["fromTs"], out var f) ? f : 0,
                    ["toTs"] = long.TryParse(q["toTs"], out var t) ? t : 0,
                    ["ok"] = q.ContainsKey("ok") ? (q["ok"].ToString() == "true" ? true : false) : null,
                    ["limit"] = int.TryParse(q["limit"], out var l) ? l : 100
                };
                return Json(CommandDispatcher.Execute("ListPrintEvents", body.ToString()));
            });
            app.MapPost("/api/print-events/archive", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("ArchivePrintEvents", body));
            });

            // ---------------------------------------------------------------- 排期 / 批次 / 召回 / 推荐
            // GET 列出计划 / POST 排计划 / DELETE 撤回计划（body 可带 now=true 提前触发）
            app.MapGet("/api/schedule", () => Json(CommandDispatcher.Execute("ListSchedulePlans", "{}")));
            app.MapPost("/api/schedule", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("SchedulePrint", body));
            });
            app.MapDelete("/api/schedule", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("RemoveSchedulePlan", body));
            });
            // 批量/波次打印：jobs 数组按序入队
            app.MapPost("/api/batch", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintBatch", body));
            });
            // 召回撤回：撤下未开始打印的任务并记审计
            app.MapPost("/api/recall", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("RecallJob", body));
            });
            // 智能推荐排期：基于真实审计流水的低峰时段
            app.MapPost("/api/recommend", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("RecommendPlan", body));
            });

            // ---- 打印管理端点（v1.01.011+）----
            // 安全打印：挂起任务列表 / 认证释放
            app.MapGet("/api/jobs/held", () => Json(CommandDispatcher.Execute("ListHeldJobs", "{}")));
            app.MapPost("/api/release", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("ReleaseJob", body));
            });
            // 配额与费用
            app.MapGet("/api/quota", async (HttpContext ctx) =>
                Json(CommandDispatcher.Execute("GetQuota", "{\"userId\":\"" +
                    (ctx.Request.Query["userId"].ToString().Replace("\\", "\\\\").Replace("\"", "\\\"")) + "\"}")));
            app.MapPost("/api/quota", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("SetQuota", body));
            });
            app.MapGet("/api/cost/report", async (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var body = "{\"userId\":\"" + Esc(q["userId"]) + "\",\"fromTs\":" +
                    (long.TryParse(q["fromTs"], out var f) ? f : 0) + ",\"toTs\":" +
                    (long.TryParse(q["toTs"], out var t) ? t : 0) + "}";
                return Json(CommandDispatcher.Execute("GetCostReport", body));
            });
            // 报表聚合
            app.MapGet("/api/report", async (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var body = "{\"fromTs\":" + (long.TryParse(q["fromTs"], out var f) ? f : 0) +
                    ",\"toTs\":" + (long.TryParse(q["toTs"], out var t) ? t : 0) +
                    ",\"groupBy\":\"" + Esc(q["groupBy"]) + "\"}";
                return Json(CommandDispatcher.Execute("GetPrintReport", body));
            });
            // 打印策略
            app.MapGet("/api/policy", () => Json(CommandDispatcher.Execute("ListPolicies", "{}")));
            app.MapPost("/api/policy", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("SetPolicy", body));
            });
            app.MapDelete("/api/policy", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("RemovePolicy", body));
            });
            // 存档 / 重放
            app.MapGet("/api/archive", async (HttpContext ctx) =>
                Json(CommandDispatcher.Execute("GetArchive", "{\"jobId\":\"" + Esc(ctx.Request.Query["jobId"]) + "\"}")));
            app.MapPost("/api/archive/clean", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("CleanArchive", body));
            });
            // 驱动部署
            app.MapGet("/api/driver/packages", () => Json(CommandDispatcher.Execute("ListDriverPackages", "{}")));
            app.MapPost("/api/driver/install", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("InstallPrinterDriver", body));
            });
            // 墨量
            app.MapGet("/api/toner", async (HttpContext ctx) =>
                Json(CommandDispatcher.Execute("GetTonerLevels", "{\"host\":\"" + Esc(ctx.Request.Query["host"]) + "\"}")));
            // 预览
            app.MapPost("/api/preview", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PreviewJob", body));
            });
            // 扫描收件箱
            app.MapGet("/api/inbox", () => Json(CommandDispatcher.Execute("ListInboxFiles", "{}")));
            app.MapPost("/api/inbox/print", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("PrintInboxFile", body));
            });

            // ---- 移动打印：Web Print 上传页 + 简化 IPP 端点 ----
            app.MapGet("/webprint", () => Results.Content(WebPrintHtml, "text/html; charset=utf-8"));
            app.MapPost("/api/webprint", async (HttpContext ctx) =>
            {
                try
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var file = form.Files.GetFile("file");
                    if (file == null || file.Length == 0)
                        return Results.Json(new { ok = false, error = "请选择文件" });
                    var dir = Path.Combine(SettingsStore.Instance.DataDir, "webprint");
                    Directory.CreateDirectory(dir);
                    var safe = Path.GetFileName(file.FileName);
                    var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + "_" + safe);
                    using (var fs = File.Create(path)) await file.CopyToAsync(fs);
                    var printer = form["printer"].ToString();
                    var body = "{\"target\":\"" + Esc(path) + "\",\"printer\":\"" + Esc(printer) +
                               "\",\"userId\":\"" + Esc(form["userId"].ToString()) + "\"}";
                    return Json(CommandDispatcher.Execute("AutoPrint", body));
                }
                catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
            });
            // IPP 简化端点：Get-Printer-Attributes（发现）/ Print-Job（提交），供 Mopria/AirPrint 类客户端探测。
            // 注意：这是 IPP 子集实现，不等于 IPP Everywhere 认证。
            app.Map("/ipp/print", async (HttpContext ctx) =>
            {
                if (string.Equals(ctx.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // 发现探测：返回打印机属性（JSON 形式，供上层映射）
                    var printers = CommandDispatcher.Execute("GetPrinterList", "{}");
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync(printers);
                    return;
                }
                if (string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        await ctx.Request.Body.CopyToAsync(ms);
                        var dir = Path.Combine(SettingsStore.Instance.DataDir, "webprint");
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, "ipp-" + Guid.NewGuid().ToString("N") + ".bin");
                        File.WriteAllBytes(path, ms.ToArray());
                        var body = "{\"target\":\"" + Esc(path) + "\"}";
                        var res = CommandDispatcher.Execute("AutoPrint", body);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.Response.WriteAsync(res);
                    }
                    catch (Exception ex)
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsync(ex.Message);
                    }
                    return;
                }
                ctx.Response.StatusCode = 405;
            });

            app.MapPost("/api/clean/scan", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("ScanDisk", body));
            });
        }

        private static string Esc(Microsoft.Extensions.Primitives.StringValues v) =>
            (v.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private const string WebPrintHtml = @"<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'><title>网页打印 · Litchio</title>
<style>body{font-family:system-ui,sans-serif;max-width:560px;margin:40px auto;padding:0 16px;color:#222}
h1{font-size:22px}input,select,button{font-size:15px;padding:10px;margin:6px 0;width:100%;box-sizing:border-box}
button{background:#c0392b;color:#fff;border:0;border-radius:6px;cursor:pointer}p{color:#666;font-size:13px}</style></head>
<body><h1>网页打印</h1><p>手机浏览器上传文档，客户端静默打印。仅在本机服务可达时使用。</p>
<form id='f'><input type='file' name='file' required><input name='printer' placeholder='打印机名（留空默认）'>
<input name='userId' placeholder='用户标识（可选，用于配额）'><button type='submit'>打印</button></form><pre id='r'></pre>
<script>document.getElementById('f').onsubmit=async e=>{e.preventDefault();const fd=new FormData(e.target);
const r=await fetch('/api/webprint',{method:'POST',body:fd});document.getElementById('r').textContent=await r.text();};</script>
</body></html>";

        public async Task StopAsync()
        {
            try
            {
                if (_app != null)
                {
                    await _app.StopAsync().ConfigureAwait(false);
                    await _app.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch { /* 忽略关闭异常 */ }
            IsRunning = false;
        }
    }
}

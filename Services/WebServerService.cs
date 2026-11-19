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
            // 只读：局域网打印机扫描（WMI 已安装网络打印机 + SSDP/WSD + mDNS）。
            // 支持 ?timeoutMs=1500&methods=installed,ssdp,mdns 限制耗时与发现方式；不装驱动、不改系统。
            app.MapGet("/api/printers/scan", async (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var timeout = int.TryParse(q["timeoutMs"], out var t) ? t : 1500;
                var methods = q["methods"].ToString();
                var body = new JObject { ["timeoutMs"] = timeout, ["methods"] = methods };
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

            app.MapPost("/api/clean/scan", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                return Json(CommandDispatcher.Execute("ScanDisk", body));
            });
        }

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

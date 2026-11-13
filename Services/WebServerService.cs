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
    /// 内嵌 ASP.NET Core (Kestrel) Web 服务：托管 docs/web 静态文档站 + REST 命令端点。
    /// 与托盘进程同生命周期，端口由 appsettings.json 配置。
    /// </summary>
    public sealed class WebServerService
    {
        private WebApplication _app;

        public string Endpoint { get; }
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

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

        private static void MapApi(WebApplication app, string docs)
        {
            app.MapGet("/api/status", () => Json(CommandDispatcher.Execute("GetStatus", "{}")));
            app.MapGet("/api/commands", () => Results.Json(new
            {
                code = 200,
                data = CommandDispatcher.ListCommands()
            }));
            app.MapGet("/api/printers", () => Json(CommandDispatcher.Execute("GetPrinterList", "{}")));
            app.MapGet("/api/queue", () => Json(CommandDispatcher.Execute("GetQueue", "{}")));
            app.MapGet("/api/disk", () => Json(CommandDispatcher.Execute("GetDiskInfo", "{}")));
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
                if (string.IsNullOrWhiteSpace(cmd))
                    return Results.Json(new { code = 400, error = "缺少 cmd 字段" });
                return Json(CommandDispatcher.Execute(cmd, body));
            });

            // 便捷端点：直接提交打印任务
            app.MapPost("/api/print", async (HttpContext ctx) =>
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
                return Json(CommandDispatcher.Execute("QueuePrint", body));
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

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLitePCL;

namespace Litchio.Headless
{
    /// <summary>队列任务（headless 精简模型）。</summary>
    public class HJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Target { get; set; } = "";
        public string Printer { get; set; } = "";
        public int Copies { get; set; } = 1;
        public bool Color { get; set; }
        public string Status { get; set; } = "pending";
        public string UserId { get; set; } = "";
        public bool ReleaseRequired { get; set; }
        public string PinHash { get; set; } = "";
        public DateTime EnqueuedAt { get; set; } = DateTime.Now;
        public string Error { get; set; } = "";
        public int Pages { get; set; }
    }

    /// <summary>打印结果。</summary>
    public class HOutcome
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Engine { get; set; } = "";
        public int Pages { get; set; }
    }

    /// <summary>
    /// 跨平台 headless 打印服务：只依赖 .NET 基础库 + SQLite + ASP.NET Core，
    /// 打印执行交给平台命令（Unix: lp/lpr，Windows: SumatraPDF 或系统打印）。
    /// 与 Windows 桌面版共用 Mulan PSL v2 协议与 REST 契约的核心子集。
    /// </summary>
    public static class Program
    {
        public const string Version = "1.01.013";
        private static readonly object Gate = new object();
        private static readonly List<HJob> Jobs = new List<HJob>();
        private static readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
        private static string _dataDir = "data";
        private static string DbPath => Path.Combine(_dataDir, "headless.db");
        private static string QueuePath => Path.Combine(_dataDir, "queue.json");

        public static async Task Main(string[] args)
        {
            Batteries.Init();
            ParseArgs(args);
            Directory.CreateDirectory(_dataDir);
            LoadQueue();
            var cts = new CancellationTokenSource();
            var worker = Task.Run(() => WorkerLoop(cts.Token));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://" + _host + ":" + _port);
            if (!string.IsNullOrEmpty(_apiToken))
                builder.Services.AddAuthorization();
            builder.Logging.ClearProviders();
            var app = builder.Build();

            if (!string.IsNullOrEmpty(_apiToken))
            {
                var token = _apiToken;
                app.Use(async (ctx, next) =>
                {
                    var path = ctx.Request.Path.Value ?? "";
                    if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase)) { await next(); return; }
                    var supplied = ctx.Request.Headers["X-Api-Token"].ToString();
                    if (string.IsNullOrEmpty(supplied))
                    {
                        var auth = ctx.Request.Headers["Authorization"].ToString();
                        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = auth.Substring(7).Trim();
                    }
                    if (string.IsNullOrEmpty(supplied)) supplied = ctx.Request.Query["token"].ToString();
                    if (!string.Equals(supplied, token, StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 401;
                        await ctx.Response.WriteAsync("{\"code\":401,\"error\":\"invalid token\"}");
                        return;
                    }
                    await next();
                });
            }

            MapApi(app);
            Console.WriteLine("Litchio headless " + Version + " listening on http://" + _host + ":" + _port);
            Console.WriteLine("Data dir: " + Path.GetFullPath(_dataDir) + " | Printer: " + (_printer ?? "(system default)"));
            await app.RunAsync();
            cts.Cancel();
            try { await worker; } catch { }
        }

        // ---------------------------------------------------------------- 配置
        private static string _host = "127.0.0.1";
        private static int _port = 19861;
        private static string _printer = "";
        private static string _apiToken = "";
        private static string _printCommand = "";

        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (a.ToLowerInvariant())
                {
                    case "--host": _host = Next(); break;
                    case "--port": int.TryParse(Next(), out _port); break;
                    case "--printer": _printer = Next(); break;
                    case "--data-dir": _dataDir = Next(); break;
                    case "--api-token": _apiToken = Next(); break;
                    case "--print-command": _printCommand = Next(); break;
                    case "--help":
                        Console.WriteLine("用法: litchio-headless [--host 127.0.0.1] [--port 19861] " +
                            "[--printer 打印机名] [--data-dir data] [--api-token token] [--print-command \"lp -d PRINTER\"]");
                        Environment.Exit(0); break;
                }
            }
            if (_host != "127.0.0.1" && _host != "localhost" && string.IsNullOrEmpty(_apiToken))
            {
                Console.Error.WriteLine("安全限制：非本机监听必须使用 --api-token 指定令牌");
                Environment.Exit(2);
            }
        }

        // ---------------------------------------------------------------- API
        private static void MapApi(WebApplication app)
        {
            app.MapGet("/health", () => Results.Json(new
            {
                status = "ok", app = "litchio-headless", version = Version,
                platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                queue = Summary()
            }));

            app.MapGet("/api/queue", () => Json(Obj(new { code = 200, data = Snapshot() })));

            app.MapPost("/api/print", async (HttpContext ctx) =>
            {
                var body = await Read(ctx);
                var o = Parse(body);
                var job = new HJob
                {
                    Target = Str(o, "target") ?? Str(o, "path") ?? Str(o, "file") ?? "",
                    Printer = Str(o, "printer") ?? _printer,
                    Copies = Math.Max(1, Int(o, "copies", 1)),
                    Color = Bool(o, "color", false),
                    UserId = Str(o, "userId") ?? "",
                    ReleaseRequired = Bool(o, "releaseRequired", false),
                    PinHash = Str(o, "pin") ?? ""
                };
                if (string.IsNullOrWhiteSpace(job.Target))
                    return Json(Obj(new { code = 400, error = "缺少 target" }));
                string file = ResolveFile(job.Target);
                if (file == null)
                    return Json(Obj(new { code = 404, error = "文件不存在: " + job.Target }));
                job.Target = file;
                if (job.ReleaseRequired && !string.IsNullOrEmpty(job.PinHash))
                    job.PinHash = Pin(job.PinHash);
                lock (Gate)
                {
                    Jobs.Add(job);
                    SaveQueue();
                }
                Signal.Release();
                return Json(Obj(new { code = 200, data = new { id = job.Id, status = job.Status, pages = job.Pages } }));
            });

            app.MapGet("/api/jobs/held", () =>
            {
                lock (Gate) return Json(Obj(new { code = 200, data = Jobs.Where(x => x.Status == "held").ToList() }));
            });

            app.MapPost("/api/release", async (HttpContext ctx) =>
            {
                var o = Parse(await Read(ctx));
                var id = Str(o, "id") ?? "";
                var pin = Str(o, "pin") ?? "";
                lock (Gate)
                {
                    var job = Jobs.FirstOrDefault(x => x.Id == id);
                    if (job == null) return Json(Obj(new { code = 404, error = "任务不存在" }));
                    if (job.Status != "held") return Json(Obj(new { code = 409, error = "任务非挂起状态" }));
                    if (!string.IsNullOrEmpty(job.PinHash) && !string.Equals(job.PinHash, Pin(pin), StringComparison.Ordinal))
                        return Json(Obj(new { code = 403, error = "PIN 错误" }));
                    job.Status = "pending";
                    SaveQueue();
                }
                Signal.Release();
                return Json(Obj(new { code = 200, data = new { id, status = "pending" } }));
            });

            app.MapGet("/api/events", async (HttpContext ctx) =>
            {
                int limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 100;
                return Json(Obj(new { code = 200, data = QueryEvents(limit) }));
            });

            app.MapGet("/api/report", async (HttpContext ctx) =>
            {
                var group = ctx.Request.Query["groupBy"].ToString();
                return Json(Obj(new { code = 200, data = Report(group) }));
            });
        }

        // ---------------------------------------------------------------- 工作循环
        private static void WorkerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HJob next = null;
                lock (Gate)
                {
                    next = Jobs.FirstOrDefault(x => x.Status == "pending");
                    if (next != null) next.Status = "printing";
                }
                if (next == null)
                {
                    Signal.Wait(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                var outcome = Execute(next);
                lock (Gate)
                {
                    next.Status = outcome.Ok ? "done" : "failed";
                    next.Error = outcome.Ok ? "" : outcome.Message;
                    next.Pages = outcome.Pages;
                    SaveQueue();
                }
                RecordEvent(next, outcome);
            }
        }

        /// <summary>跨平台打印执行：优先自定义命令，Unix 用 lp/lpr，Windows 用 SumatraPDF 或 Shell。</summary>
        private static HOutcome Execute(HJob job)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_printCommand))
                    return RunCommand(Expand(_printCommand, job), job);
                if (OperatingSystem.IsWindows())
                {
                    var exe = Path.Combine(AppContext.BaseDirectory, "SumatraPDF.exe");
                    if (File.Exists(exe))
                    {
                        var psi = new ProcessStartInfo(exe)
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        };
                        psi.ArgumentList.Add("-silent");
                        psi.ArgumentList.Add("-exit-when-done");
                        psi.ArgumentList.Add("-print-settings");
                        psi.ArgumentList.Add(job.Copies + "x," + (job.Color ? "color" : "monochrome"));
                        psi.ArgumentList.Add("-print-to");
                        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(job.Printer) ? "Microsoft Print to PDF" : job.Printer);
                        psi.ArgumentList.Add(job.Target);
                        return Run(psi, "SumatraPDF", job);
                    }
                    return new HOutcome { Ok = false, Message = "Windows 需要在程序目录放置 SumatraPDF.exe 或指定 --print-command" };
                }
                // Unix: lp -d <printer> -n <copies> <file>
                var args = new List<string>();
                if (!string.IsNullOrWhiteSpace(job.Printer)) { args.Add("-d"); args.Add(job.Printer); }
                args.Add("-n"); args.Add(job.Copies.ToString());
                if (job.Color) args.Add("-o"); args.Add("print-color-mode=color");
                args.Add(job.Target);
                var lpsi = new ProcessStartInfo("lp") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) lpsi.ArgumentList.Add(a);
                return Run(lpsi, "lp", job);
            }
            catch (Exception ex) { return new HOutcome { Ok = false, Message = ex.Message }; }
        }

        private static HOutcome RunCommand(string command, HJob job)
        {
            // 自定义命令按 shell 解析，支持 "echo {file} > out.txt" 这类带参数/重定向的写法
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
            return Run(psi, "custom", job);
        }

        private static HOutcome Run(ProcessStartInfo psi, string engine, HJob job)
        {
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return new HOutcome { Ok = false, Message = "无法启动打印命令" };
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch { } }
                var detail = (outTask.GetAwaiter().GetResult() + "\n" + errTask.GetAwaiter().GetResult()).Trim();
                if (p.ExitCode != 0)
                    return new HOutcome { Ok = false, Engine = engine, Message = "打印命令退出码 " + p.ExitCode + ": " + detail };
                var pages = CountPages(job.Target);
                return new HOutcome { Ok = true, Engine = engine, Message = "已提交打印" + (detail.Length > 0 ? ": " + detail : ""), Pages = pages };
            }
            catch (Exception ex) { return new HOutcome { Ok = false, Engine = engine, Message = ex.Message }; }
        }

        private static string Expand(string template, HJob job) => template
            .Replace("{printer}", string.IsNullOrWhiteSpace(job.Printer) ? "" : Quote(job.Printer))
            .Replace("{copies}", job.Copies.ToString())
            .Replace("{file}", Quote(job.Target));

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        private static int CountPages(string file)
        {
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
                    int idx = 0, best = 0;
                    while ((idx = text.IndexOf("/Count", idx, StringComparison.Ordinal)) >= 0)
                    {
                        int p = idx + 6;
                        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                        int s = p;
                        while (p < text.Length && char.IsDigit(text[p])) p++;
                        if (p > s && int.TryParse(text.Substring(s, p - s), out var n) && n > best) best = n;
                        idx = p;
                    }
                    return best;
                }
                if (IsImage(ext)) return 1;
                if (ext == ".txt" || ext == ".csv" || ext == ".log")
                    return Math.Max(1, (int)Math.Ceiling(File.ReadLines(file).Count() / 45.0));
            }
            catch { }
            return 0;
        }

        private static bool IsImage(string e) =>
            e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif" || e == ".tif" || e == ".tiff";

        // ---------------------------------------------------------------- 审计
        private static void RecordEvent(HJob job, HOutcome outcome)
        {
            try
            {
                using var c = OpenDb();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO events(ts,machine,host,engine,pages,copies,ok,detail,user_id)" +
                                  " VALUES($t,$m,$h,$e,$p,$c,$o,$d,$u)";
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$m", Dns.GetHostName());
                cmd.Parameters.AddWithValue("$h", job.Printer ?? "");
                cmd.Parameters.AddWithValue("$e", outcome.Engine ?? "");
                cmd.Parameters.AddWithValue("$p", outcome.Pages);
                cmd.Parameters.AddWithValue("$c", job.Copies);
                cmd.Parameters.AddWithValue("$o", outcome.Ok ? 1 : 0);
                cmd.Parameters.AddWithValue("$d", outcome.Message ?? "");
                cmd.Parameters.AddWithValue("$u", job.UserId ?? "");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.Error.WriteLine("审计写入失败: " + ex.Message); }
        }

        private static List<JObject> QueryEvents(int limit)
        {
            var list = new List<JObject>();
            try
            {
                using var c = OpenDb();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT ts,machine,host,engine,pages,copies,ok,detail,user_id FROM events ORDER BY ts DESC LIMIT $l";
                cmd.Parameters.AddWithValue("$l", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new JObject
                    {
                        ["ts"] = r.GetInt64(0), ["machine"] = r.GetString(1), ["host"] = r.GetString(2),
                        ["engine"] = r.GetString(3), ["pages"] = r.GetInt32(4), ["copies"] = r.GetInt32(5),
                        ["ok"] = r.GetInt64(6) == 1, ["detail"] = r.GetString(7), ["userId"] = r.GetString(8)
                    });
            }
            catch { }
            return list;
        }

        private static List<JObject> Report(string group)
        {
            var key = (group ?? "host").ToLowerInvariant();
            if (key != "host" && key != "user_id" && key != "engine") key = "host";
            var list = new List<JObject>();
            try
            {
                using var c = OpenDb();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT " + key + " AS k, COUNT(*), SUM(pages), SUM(CASE WHEN ok=0 THEN 1 ELSE 0 END)" +
                                  " FROM events GROUP BY k ORDER BY k";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new JObject
                    {
                        ["key"] = r.IsDBNull(0) ? "-" : r.GetString(0), ["jobs"] = r.GetInt32(1),
                        ["pages"] = r.IsDBNull(2) ? 0 : r.GetInt32(2), ["failed"] = r.GetInt32(3)
                    });
            }
            catch { }
            return list;
        }

        private static SqliteConnection OpenDb()
        {
            var c = new SqliteConnection("Data Source=" + DbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS events(ts INTEGER NOT NULL, machine TEXT NOT NULL," +
                " host TEXT NOT NULL, engine TEXT NOT NULL, pages INTEGER NOT NULL DEFAULT 0," +
                " copies INTEGER NOT NULL DEFAULT 1, ok INTEGER NOT NULL DEFAULT 1," +
                " detail TEXT NOT NULL DEFAULT '', user_id TEXT NOT NULL DEFAULT '')";
            cmd.ExecuteNonQuery();
            return c;
        }

        // ---------------------------------------------------------------- 工具
        private static string ResolveFile(string target)
        {
            if (File.Exists(target)) return Path.GetFullPath(target);
            var p = Path.Combine(_dataDir, "inbox", Path.GetFileName(target ?? ""));
            return File.Exists(p) ? Path.GetFullPath(p) : null;
        }

        private static string Pin(string pin)
        {
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes("litchio-headless-salt"));
            return Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pin, salt, 10000, HashAlgorithmName.SHA256, 32));
        }

        private static object Snapshot()
        {
            lock (Gate) return Jobs.Select(j => new
            {
                j.Id, j.Target, j.Printer, j.Copies, j.Status, j.UserId,
                j.ReleaseRequired, j.Pages, j.Error, j.EnqueuedAt
            }).ToList();
        }

        private static object Summary()
        {
            lock (Gate) return new
            {
                total = Jobs.Count, pending = Jobs.Count(x => x.Status == "pending"),
                printing = Jobs.Count(x => x.Status == "printing"),
                held = Jobs.Count(x => x.Status == "held"),
                done = Jobs.Count(x => x.Status == "done"),
                failed = Jobs.Count(x => x.Status == "failed")
            };
        }

        private static void LoadQueue()
        {
            try
            {
                if (!File.Exists(QueuePath)) return;
                var list = JsonConvert.DeserializeObject<List<HJob>>(File.ReadAllText(QueuePath));
                if (list == null) return;
                lock (Gate)
                {
                    Jobs.Clear();
                    foreach (var j in list)
                    {
                        if (j.Status == "printing") { j.Status = "pending"; j.Error = "服务重启恢复"; }
                        Jobs.Add(j);
                        if (j.Status == "pending") Signal.Release();
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("队列加载失败: " + ex.Message); }
        }

        private static void SaveQueue()
        {
            try
            {
                string json;
                lock (Gate) json = JsonConvert.SerializeObject(Jobs, Formatting.Indented);
                File.WriteAllText(QueuePath, json);
            }
            catch { }
        }

        private static IResult Json(object o) => Results.Content(JsonConvert.SerializeObject(o), "application/json; charset=utf-8");
        private static JObject Obj(object o) => JObject.FromObject(o);
        private static async Task<string> Read(HttpContext ctx)
        {
            using var r = new StreamReader(ctx.Request.Body);
            var s = await r.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(s) ? "{}" : s;
        }
        private static JObject Parse(string s)
        {
            try { return JObject.Parse(s); } catch { return new JObject(); }
        }
        private static string Str(JObject o, string k) => o?[k]?.Type == JTokenType.String ? o[k].Value<string>() : null;
        private static int Int(JObject o, string k, int d) => o?[k] != null && int.TryParse(o[k].ToString(), out var v) ? v : d;
        private static bool Bool(JObject o, string k, bool d) => o?[k] != null && bool.TryParse(o[k].ToString(), out var v) ? v : d;
    }
}

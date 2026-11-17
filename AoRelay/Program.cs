// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
//
// AoRelay — Active-Online Print 中继服务器。
// 部署在中继节点（公网或内网可达），负责打印终端与业务方之间的设备发现与任务路由。
// 传输层为 ASP.NET Core 原生 WebSocket，HTTP 与 WS 共用同一端口：
//   WS  /device  打印终端（PipPrinter）出站长连接：注册、心跳、接收任务、回传结果
//   WS  /client  业务方（网页 / 中台）连接：设备发现、下发任务、接收结果
//   GET /api/devices  在线设备列表（JSON）
//   GET /ao.html     浏览器测试页
//   GET /health      健康检查
// 用法: dotnet AoRelay.dll [--port 8080] [--key <shared-secret>]
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AoRelay
{
    internal sealed class DeviceSession
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public List<string> Printers = new List<string>();
        public DateTime ConnectedAt = DateTime.UtcNow;
        public DateTime LastSeen = DateTime.UtcNow;
        public WebSocket Socket;
    }

    internal sealed class ClientSession
    {
        public string Id = "";
        public DateTime ConnectedAt = DateTime.UtcNow;
        public WebSocket Socket;
    }

    internal sealed class JobTicket
    {
        public string JobId = "";
        public string DeviceId = "";
        public string ClientId = "";
        public DateTime CreatedAt = DateTime.UtcNow;
    }

    public static class Program
    {
        // 心跳超时：设备 90 秒未上报即视为离线
        private const int DeviceTimeoutSeconds = 90;
        // 任务超时：下发后 120 秒未回传即向业务方返回错误
        private const int JobTimeoutSeconds = 120;

        private static readonly ConcurrentDictionary<string, DeviceSession> Devices =
            new ConcurrentDictionary<string, DeviceSession>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ClientSession> Clients =
            new ConcurrentDictionary<string, ClientSession>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, JobTicket> Jobs =
            new ConcurrentDictionary<string, JobTicket>(StringComparer.OrdinalIgnoreCase);

        // 发送串行化：同一 WebSocket 不允许并发发送
        private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> SendGates =
            new ConcurrentDictionary<WebSocket, SemaphoreSlim>();

        private static string _key = "";
        private static int _port = 8080;

        public static async Task Main(string[] args)
        {
            ParseArgs(args);
            Console.WriteLine($"[AoRelay] http://127.0.0.1:{_port}  (ao.html / api/devices / health)");
            Console.WriteLine($"[AoRelay] ws://127.0.0.1:{_port}     (device / client)");

            var builder = WebApplication.CreateBuilder();
            var app = builder.Build();
            app.Urls.Add($"http://0.0.0.0:{_port}");
            app.UseWebSockets();

            app.MapGet("/health", () => Results.Text("ok", "text/plain"));
            // 注意：BuildDeviceList() 返回 Newtonsoft JArray，必须显式 ToString 后以 JSON 文本返回；
            // 直接 Results.Json 会走 System.Text.Json，把 JArray 序列化成嵌套数组而非对象数组。
            app.MapGet("/api/devices", () =>
                Results.Content(BuildDeviceList().ToString(Formatting.None), "application/json"));
            app.MapGet("/ao.html", async ctx =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(TestPage.Html.Replace("__WS_PORT__", _port.ToString()));
            });
            app.Map("/device", async ctx => await HandleDevice(ctx));
            app.Map("/client", async ctx => await HandleClient(ctx));

            // 后台任务：设备离线清扫 + 任务超时
            var cts = new CancellationTokenSource();
            var sweeper = Task.Run(() => SweepLoop(cts.Token));

            await app.StartAsync();
            Console.WriteLine("[AoRelay] 服务已启动");

            try
            {
                await app.WaitForShutdownAsync();
            }
            finally
            {
                cts.Cancel();
                try { await sweeper; } catch { /* 忽略 */ }
                await app.StopAsync();
            }
        }

        // ---------------------------------------------------------------- 参数

        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[++i], out _port);
                else if (a.Equals("--key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    _key = args[++i];
                else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(a.Substring("--port=".Length), out _port);
                else if (a.StartsWith("--key=", StringComparison.OrdinalIgnoreCase))
                    _key = a.Substring("--key=".Length);
            }
            if (_port <= 0 || _port > 65535) _port = 8080;
        }

        // ---------------------------------------------------------------- 设备端

        private static async Task HandleDevice(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("需要 WebSocket 升级请求");
                return;
            }

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new DeviceSession
            {
                ConnectedAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Socket = socket
            };
            var registered = false;
            Console.WriteLine($"[device] 连接打开 {ctx.Connection.RemoteIpAddress}");

            try
            {
                await ReceiveLoop(socket, message =>
                {
                    var json = JObject.Parse(message ?? "");
                    switch (json["type"]?.ToString())
                    {
                        case "hello":
                            session.Id = !string.IsNullOrWhiteSpace(json["id"]?.ToString())
                                ? json["id"].ToString()
                                : Guid.NewGuid().ToString("N");
                            session.Name = json["name"]?.ToString() ?? session.Id;
                            session.Version = json["version"]?.ToString() ?? "";
                            session.Printers = json["printers"]?.ToObject<List<string>>() ?? new List<string>();
                            session.ConnectedAt = DateTime.UtcNow;
                            session.LastSeen = DateTime.UtcNow;

                            if (!string.IsNullOrEmpty(_key) &&
                                !string.Equals(json["key"]?.ToString() ?? "", _key, StringComparison.Ordinal))
                            {
                                SendAsync(socket, Error("unauthorized: bad key")).GetAwaiter().GetResult();
                                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "unauthorized", CancellationToken.None)
                                    .GetAwaiter().GetResult();
                                return;
                            }

                            // 同一设备重连时踢掉旧连接
                            if (Devices.TryGetValue(session.Id, out var old) && old != null && old.Socket != socket)
                            {
                                try
                                {
                                    SendAsync(old.Socket, Error("replaced by a new connection")).GetAwaiter().GetResult();
                                    old.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
                                        .GetAwaiter().GetResult();
                                }
                                catch { /* 忽略 */ }
                            }
                            Devices[session.Id] = session;
                            registered = true;
                            SendAsync(socket, $"{{\"type\":\"welcome\",\"deviceId\":\"{session.Id}\",\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")
                                .GetAwaiter().GetResult();
                            Console.WriteLine($"[device] 注册: {session.Id} ({session.Name}) 打印机 {session.Printers.Count} 台");
                            BroadcastDeviceList();
                            break;

                        case "ping":
                            session.LastSeen = DateTime.UtcNow;
                            SendAsync(socket, $"{{\"type\":\"pong\",\"t\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")
                                .GetAwaiter().GetResult();
                            break;

                        case "result":
                            var jobId = json["jobId"]?.ToString();
                            if (!string.IsNullOrEmpty(jobId) && Jobs.TryRemove(jobId, out var ticket))
                            {
                                var response = json["response"]?.ToString() ?? "{}";
                                if (Clients.TryGetValue(ticket.ClientId, out var client) && client != null)
                                {
                                    SendAsync(client.Socket,
                                        $"{{\"type\":\"result\",\"jobId\":\"{Escape(jobId)}\",\"deviceId\":\"{Escape(ticket.DeviceId)}\",\"response\":{Embed(response)}}}")
                                        .GetAwaiter().GetResult();
                                }
                            }
                            break;
                    }
                });
            }
            catch (JsonException) { /* 忽略单条坏消息 */ }
            catch (Exception ex) { Console.WriteLine($"[device] 连接异常: {ex.Message}"); }
            finally
            {
                if (registered && !string.IsNullOrEmpty(session.Id))
                {
                    Devices.TryRemove(session.Id, out _);
                    Console.WriteLine($"[device] 离线: {session.Id} ({session.Name})");
                    BroadcastDeviceList();
                }
                SendGates.TryRemove(socket, out _);
            }
        }

        // ---------------------------------------------------------------- 客户端

        private static async Task HandleClient(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("需要 WebSocket 升级请求");
                return;
            }

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new ClientSession
            {
                Id = Guid.NewGuid().ToString("N"),
                ConnectedAt = DateTime.UtcNow,
                Socket = socket
            };
            Clients[session.Id] = session;
            Console.WriteLine($"[client] 连接: {session.Id}");
            SendAsync(socket, $"{{\"type\":\"welcome\",\"clientId\":\"{session.Id}\",\"devices\":{BuildDeviceList()}}}")
                .GetAwaiter().GetResult();

            try
            {
                await ReceiveLoop(socket, message =>
                {
                    var json = JObject.Parse(message ?? "");
                    switch (json["type"]?.ToString())
                    {
                        case "list":
                            SendAsync(socket, $"{{\"type\":\"devices\",\"devices\":{BuildDeviceList()}}}").GetAwaiter().GetResult();
                            break;

                        case "job":
                            var deviceId = json["deviceId"]?.ToString() ?? "";
                            var jobId = !string.IsNullOrWhiteSpace(json["jobId"]?.ToString())
                                ? json["jobId"].ToString()
                                : Guid.NewGuid().ToString("N");
                            var cmd = json["cmd"]?.ToString() ?? "";
                            var payload = json["payload"]?.ToString() ?? "{}";

                            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(cmd))
                            {
                                SendAsync(socket, $"{{\"type\":\"error\",\"jobId\":\"{Escape(jobId)}\",\"error\":\"deviceId 与 cmd 必填\"}}")
                                    .GetAwaiter().GetResult();
                                break;
                            }
                            if (!Devices.TryGetValue(deviceId, out var device) || device == null)
                            {
                                SendAsync(socket, $"{{\"type\":\"error\",\"jobId\":\"{Escape(jobId)}\",\"error\":\"设备不在线: {Escape(deviceId)}\"}}")
                                    .GetAwaiter().GetResult();
                                break;
                            }

                            Jobs[jobId] = new JobTicket { JobId = jobId, DeviceId = deviceId, ClientId = session.Id };
                            SendAsync(device.Socket,
                                $"{{\"type\":\"job\",\"jobId\":\"{Escape(jobId)}\",\"cmd\":\"{Escape(cmd)}\",\"payload\":{Embed(payload)}}}")
                                .GetAwaiter().GetResult();
                            SendAsync(socket, $"{{\"type\":\"accepted\",\"jobId\":\"{Escape(jobId)}\",\"deviceId\":\"{Escape(deviceId)}\"}}")
                                .GetAwaiter().GetResult();
                            Console.WriteLine($"[job] {session.Id} → {deviceId} : {cmd} ({jobId})");
                            break;

                        case "ping":
                            SendAsync(socket, $"{{\"type\":\"pong\",\"t\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")
                                .GetAwaiter().GetResult();
                            break;
                    }
                });
            }
            catch (JsonException) { /* 忽略单条坏消息 */ }
            catch (Exception ex) { Console.WriteLine($"[client] 连接异常: {ex.Message}"); }
            finally
            {
                Clients.TryRemove(session.Id, out _);
                Console.WriteLine($"[client] 断开: {session.Id}");
                SendGates.TryRemove(socket, out _);
            }
        }

        // ---------------------------------------------------------------- 收发

        private static async Task ReceiveLoop(WebSocket socket, Action<string> onMessage)
        {
            var buffer = new byte[16384];
            while (socket.State == WebSocketState.Open)
            {
                string text;
                try
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);
                    text = sb.ToString();
                }
                catch (WebSocketException) { return; }
                catch (ObjectDisposedException) { return; }

                try { onMessage(text); }
                catch (Exception ex) { Console.WriteLine($"[relay] 消息处理异常: {ex.Message}"); }
            }
        }

        private static async Task SendAsync(WebSocket socket, string payload)
        {
            if (socket == null || socket.State != WebSocketState.Open) return;
            var gate = SendGates.GetOrAdd(socket, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (socket.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(payload);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) { Console.WriteLine($"[relay] 发送失败: {ex.Message}"); }
            finally { gate.Release(); }
        }

        // ---------------------------------------------------------------- 辅助

        private static JArray BuildDeviceList()
        {
            var arr = new JArray();
            foreach (var d in Devices.Values.OrderBy(x => x.Name))
            {
                arr.Add(new JObject
                {
                    ["id"] = d.Id,
                    ["name"] = d.Name,
                    ["version"] = d.Version,
                    ["printers"] = new JArray(d.Printers),
                    ["connectedAt"] = new DateTimeOffset(d.ConnectedAt).ToUnixTimeSeconds(),
                    ["lastSeen"] = new DateTimeOffset(d.LastSeen).ToUnixTimeSeconds()
                });
            }
            return arr;
        }

        private static void BroadcastDeviceList()
        {
            var payload = $"{{\"type\":\"devices\",\"devices\":{BuildDeviceList()}}}";
            foreach (var c in Clients.Values.ToList())
            {
                _ = SendAsync(c.Socket, payload);
            }
        }

        private static async Task SweepLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    foreach (var d in Devices.Values.ToList())
                    {
                        if ((now - d.LastSeen).TotalSeconds > DeviceTimeoutSeconds)
                        {
                            if (Devices.TryRemove(d.Id, out _))
                            {
                                Console.WriteLine($"[device] 心跳超时移除: {d.Id}");
                                try { await d.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "timeout", ct); } catch { }
                                BroadcastDeviceList();
                            }
                        }
                    }

                    foreach (var job in Jobs.Values.ToList())
                    {
                        if ((now - job.CreatedAt).TotalSeconds > JobTimeoutSeconds)
                        {
                            if (Jobs.TryRemove(job.JobId, out _))
                            {
                                if (Clients.TryGetValue(job.ClientId, out var client) && client != null)
                                {
                                    await SendAsync(client.Socket,
                                        $"{{\"type\":\"error\",\"jobId\":\"{Escape(job.JobId)}\",\"error\":\"job timeout\"}}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[sweep] 异常: {ex.Message}");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { break; }
            }
        }

        private static string Error(string message) =>
            JsonConvert.SerializeObject(new { type = "error", error = message });

        private static string Escape(string s) =>
            JsonConvert.SerializeObject(s ?? "").Trim('"');

        /// <summary>把内嵌 JSON 字符串原样嵌入外层对象（已是 JSON 文本时直接拼接）。</summary>
        private static string Embed(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            try { JToken.Parse(json); return json; }
            catch { return JsonConvert.SerializeObject(json); }
        }
    }
}

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// Active-Online Print 客户端（AO 终端）。
    /// 主动向中继服务器（AoRelay）建立出站 WebSocket 长连接：
    ///   · hello 注册本机设备（机器名、打印机列表、版本），中继据此把本机列入在线设备表
    ///   · 30 秒心跳（ping/pong），中继 90 秒未收到即判定离线
    ///   · 收到中继路由的 job → 交给 CommandDispatcher 在本机执行 → 回传 result
    ///   · 断线后指数退避自动重连（1s→2s→4s→…→30s 封顶）
    /// 仅在配置 AoEnabled=true 且 AoRelayUrl 非空时启动。
    /// 传输层使用 BCL 的 ClientWebSocket（Fleck 1.2 仅提供服务端实现）。
    /// </summary>
    public sealed class AoClientService
    {
        private readonly object _gate = new object();
        private ClientWebSocket _socket;
        private System.Threading.Timer _heartbeat;
        private CancellationTokenSource _cts;
        private Task _loop;
        private readonly SemaphoreSlim _sendSem = new SemaphoreSlim(1, 1);
        private int _reconnectAttempt;
        private bool _running;

        public static AoClientService Instance { get; } = new AoClientService();

        /// <summary>当前是否与中继保持连接。</summary>
        public bool Online
        {
            get
            {
                lock (_gate) return _socket?.State == WebSocketState.Open;
            }
        }

        /// <summary>本终端在中继侧的设备 ID（hello 时上报）。</summary>
        public string DeviceId { get; private set; } = "";

        private AoClientService() { }

        public void Start()
        {
            var s = SettingsStore.Instance.Current;
            if (!s.AoEnabled || string.IsNullOrWhiteSpace(s.AoRelayUrl))
            {
                Tools.Logger("[AO] 未启用或未配置中继地址，跳过启动");
                return;
            }
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            Tools.Logger("[AO] 客户端已启动，目标中继: " + s.AoRelayUrl);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _cts?.Cancel(); } catch { /* 忽略 */ }
            try { _heartbeat?.Dispose(); } catch { /* 忽略 */ }
            try
            {
                lock (_gate)
                {
                    if (_socket != null)
                    {
                        try { _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped", CancellationToken.None)
                                    .GetAwaiter().GetResult(); }
                        catch { /* 忽略 */ }
                        _socket.Dispose();
                        _socket = null;
                    }
                }
            }
            catch { /* 忽略 */ }
            try { _loop?.Wait(2000); } catch { /* 忽略 */ }
            Tools.Logger("[AO] 客户端已停止");
        }

        // ---------------------------------------------------------------- 连接循环

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var socket = new ClientWebSocket();
                    // 协议层保活，避免 NAT/防火墙静默断链
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await socket.ConnectAsync(new Uri(BuildDeviceUrl()), ct);

                    lock (_gate) _socket = socket;
                    _reconnectAttempt = 0;
                    StartHeartbeat();
                    SendHello();
                    Tools.Logger("[AO] 已连接中继，发送 hello");

                    await ReceiveLoop(socket, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Tools.Logger("[AO] 连接异常: " + ex.Message);
                }
                finally
                {
                    StopHeartbeat();
                    lock (_gate)
                    {
                        try { _socket?.Dispose(); } catch { /* 忽略 */ }
                        _socket = null;
                    }
                }

                if (ct.IsCancellationRequested) break;

                // 指数退避：1s, 2s, 4s, 8s, 16s, 30s 封顶
                var delay = Math.Min(30000, 1000 * (1 << Math.Min(_reconnectAttempt, 5)));
                _reconnectAttempt++;
                Tools.Logger($"[AO] {delay / 1000}s 后重连…");
                try { await Task.Delay(delay, ct); } catch { break; }
            }
        }

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16384];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                string text;
                try
                {
                    var sb = new StringBuilder();
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);
                    text = sb.ToString();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Tools.Logger("[AO] 接收异常: " + ex.Message);
                    return;
                }

                try { OnMessage(text); }
                catch (Exception ex) { Tools.Logger("[AO] 消息处理异常: " + ex); }
            }
        }

        private string BuildDeviceUrl()
        {
            var s = SettingsStore.Instance.Current;
            var url = s.AoRelayUrl.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "ws://" + url.Substring(7);
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "wss://" + url.Substring(8);
            if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                url = "ws://" + url;
            return url.TrimEnd('/') + "/device";
        }

        // ---------------------------------------------------------------- 协议

        private void SendHello()
        {
            var s = SettingsStore.Instance.Current;
            DeviceId = BuildDeviceId();

            List<string> printers;
            try { printers = new PrinterService().GetNames(); }
            catch (Exception ex)
            {
                Tools.Logger("[AO] 获取打印机列表失败: " + ex.Message);
                printers = new List<string>();
            }

            var hello = new JObject
            {
                ["type"] = "hello",
                ["id"] = DeviceId,
                ["name"] = string.IsNullOrWhiteSpace(s.AoDeviceName) ? DeviceId : s.AoDeviceName,
                ["version"] = Brand.Version,
                ["printers"] = new JArray(printers),
                ["key"] = s.AoKey ?? ""
            };
            _ = SendSafe(hello.ToString(Formatting.None));
        }

        /// <summary>设备 ID：优先使用设置的 AoDeviceName，否则用机器名（同一中继内唯一）。</summary>
        private string BuildDeviceId()
        {
            var s = SettingsStore.Instance.Current;
            var raw = string.IsNullOrWhiteSpace(s.AoDeviceName) ? Environment.MachineName : s.AoDeviceName;
            // 只保留中继协议安全字符，避免空格/引号破坏 JSON 路由
            var safe = "";
            foreach (var ch in raw)
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '@') safe += ch;
            return string.IsNullOrEmpty(safe) ? "device-" + Environment.MachineName : safe;
        }

        private void OnMessage(string message)
        {
            var json = JObject.Parse(message ?? "");
            switch (json["type"]?.ToString())
            {
                case "welcome":
                    Tools.Logger("[AO] 中继已接受注册: " + (json["deviceId"]?.ToString() ?? ""));
                    break;
                case "pong":
                    break;
                case "job":
                    HandleJob(json);
                    break;
                case "error":
                    Tools.Logger("[AO] 中继错误: " + (json["error"]?.ToString() ?? ""));
                    break;
                default:
                    Tools.Logger("[AO] 未知消息类型: " + (json["type"]?.ToString() ?? ""));
                    break;
            }
        }

        private void HandleJob(JObject json)
        {
            var jobId = json["jobId"]?.ToString() ?? "";
            var cmd = json["cmd"]?.ToString() ?? "";
            var payload = json["payload"]?.ToString() ?? "{}";

            Tools.Logger($"[AO] 收到任务 {jobId} : {cmd}");
            // 打印可能耗时，放线程池执行，避免阻塞 WebSocket 接收循环
            Task.Run(async () =>
            {
                var response = CommandDispatcher.Execute(cmd, payload);
                try
                {
                    var result = new JObject { ["type"] = "result", ["jobId"] = jobId };
                    try { result["response"] = JToken.Parse(response); }
                    catch { result["response"] = response; }
                    await SendSafe(result.ToString(Formatting.None));
                    Tools.Logger($"[AO] 任务 {jobId} 已回传结果");
                }
                catch (Exception ex)
                {
                    Tools.Logger($"[AO] 回传结果失败 {jobId}: {ex.Message}");
                }
            });
        }

        // ---------------------------------------------------------------- 心跳 / 发送

        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeat = new System.Threading.Timer(
                _ => _ = SendSafe("{\"type\":\"ping\"}"),
                null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void StopHeartbeat()
        {
            try { _heartbeat?.Dispose(); } catch { /* 忽略 */ }
            _heartbeat = null;
        }

        /// <summary>串行化发送（ClientWebSocket 不允许并发发送）。</summary>
        private async Task SendSafe(string payload)
        {
            ClientWebSocket socket;
            lock (_gate) socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            await _sendSem.WaitAsync();
            try
            {
                if (socket.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(payload);
                await socket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Tools.Logger("[AO] 发送失败: " + ex.Message);
            }
            finally
            {
                _sendSem.Release();
            }
        }
    }
}

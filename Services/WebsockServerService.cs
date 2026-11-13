using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DuoAssistant.Utils;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// WebSocket 服务端（Fleck）。保留原有 {cmd, ...} JSON 协议与 Result 响应结构，
    /// 但把反射分派收敛到 CommandDispatcher 白名单，并主动推送队列变更通知。
    /// </summary>
    public class WebsockServerService
    {
        private readonly object _gate = new object();
        private readonly List<IWebSocketConnection> _sockets = new List<IWebSocketConnection>();
        private WebSocketServer _server;

        public string Endpoint { get; }

        public WebsockServerService()
        {
            var s = SettingsStore.Instance.Current;
            Endpoint = "ws://" + s.WsHost + ":" + s.WsPort;
        }

        public void Start()
        {
            PrintQueueService.Instance.Changed += (_, __) => BroadcastQueueChanged();
            _server = new WebSocketServer(Endpoint) { RestartAfterListenError = true };
            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (_gate) _sockets.Add(socket);
                    Tools.Logger("WebSocket 客户端已连接: " + socket.ConnectionInfo?.ClientIpAddress);
                    socket.Send(Result.success(msg: "connect the print successful", cmd: "startPrint"));
                };
                socket.OnClose = () =>
                {
                    lock (_gate) _sockets.Remove(socket);
                    Tools.Logger("WebSocket 客户端已断开");
                };
                socket.OnMessage = message => OnMessage(socket, message);
                socket.OnError = ex => Tools.Logger("WebSocket 错误: " + ex.Message);
            });
            Tools.Logger("WebSocket 服务已启动: " + Endpoint);
        }

        private void OnMessage(IWebSocketConnection socket, string message)
        {
            string cmd = null;
            try
            {
                var token = JToken.Parse(message ?? "");
                cmd = token?["cmd"]?.ToString();
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    socket.Send(Result.error("缺少 cmd 字段", cmd: "Notify"));
                    return;
                }
                var response = CommandDispatcher.Execute(cmd, message);
                socket.Send(response);
            }
            catch (JsonException)
            {
                socket.Send(Result.error("请求不是合法 JSON", cmd: "Notify"));
            }
            catch (Exception ex)
            {
                Tools.Logger("WebSocket 消息处理异常: " + ex);
                socket.Send(Result.error(ex.Message, 500, cmd ?? "Notify"));
            }
        }

        /// <summary>队列变化时向所有连接推送轻量快照，UI 可实时刷新。</summary>
        public void BroadcastQueueChanged()
        {
            var payload = Result.success(
                data: new
                {
                    summary = PrintQueueService.Instance.Summary(),
                    jobs = PrintQueueService.Instance.GetAll()
                },
                cmd: "QueueChanged");
            Broadcast(payload);
        }

        public void Broadcast(string payload)
        {
            List<IWebSocketConnection> snapshot;
            lock (_gate) snapshot = _sockets.ToList();
            foreach (var s in snapshot)
            {
                try { s.Send(payload); }
                catch (Exception ex) { Tools.Logger("广播失败: " + ex.Message); }
            }
        }

        public void Stop()
        {
            try { _server?.Dispose(); } catch { /* 忽略 */ }
        }
    }
}

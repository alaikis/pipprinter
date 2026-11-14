// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// ASP.NET Core 原生 WebSocket 连接中心。
    /// 端点 ws://&lt;host&gt;:&lt;port&gt;/ws，与 Web 文档/REST 共用同一 Kestrel 端口。
    /// 协议保持 {cmd, ...} JSON + Result 响应结构，与旧 Fleck 实现完全兼容。
    /// </summary>
    public sealed class WsHub
    {
        private readonly object _gate = new object();
        private readonly List<WsSession> _sessions = new List<WsSession>();

        public int Count { get { lock (_gate) return _sessions.Count; } }

        private sealed class WsSession
        {
            public string Id = Guid.NewGuid().ToString("N");
            public WebSocket Socket;
            public DateTime ConnectedAt = DateTime.UtcNow;
        }

        /// <summary>处理一个 WebSocket 升级请求；非升级请求返回 400。</summary>
        public async Task Handle(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("需要 WebSocket 升级请求");
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = new WsSession { Socket = socket };
            lock (_gate) _sessions.Add(session);
            Tools.Logger($"WebSocket 客户端已连接: {context.Connection.RemoteIpAddress} ({session.Id})");
            Send(session, Result.success(msg: "connect the print successful", cmd: "startPrint"));

            try
            {
                await ReceiveLoop(session);
            }
            finally
            {
                lock (_gate) _sessions.Remove(session);
                Tools.Logger($"WebSocket 客户端已断开: {session.Id}");
            }
        }

        private async Task ReceiveLoop(WsSession session)
        {
            var buffer = new byte[16384];
            while (session.Socket.State == WebSocketState.Open)
            {
                string text;
                try
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await session.Socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);
                    text = sb.ToString();
                }
                catch (WebSocketException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (OperationCanceledException) { return; }

                try { OnMessage(session, text); }
                catch (Exception ex) { Tools.Logger("WebSocket 消息处理异常: " + ex); }
            }
        }

        private void OnMessage(WsSession session, string message)
        {
            string cmd = null;
            try
            {
                var token = JToken.Parse(message ?? "");
                cmd = token?["cmd"]?.ToString();
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    Send(session, Result.error("缺少 cmd 字段", cmd: "Notify"));
                    return;
                }
                var response = CommandDispatcher.Execute(cmd, message);
                Send(session, response);
            }
            catch (JsonException)
            {
                Send(session, Result.error("请求不是合法 JSON", cmd: "Notify"));
            }
            catch (Exception ex)
            {
                Tools.Logger("WebSocket 消息处理异常: " + ex);
                Send(session, Result.error(ex.Message, 500, cmd ?? "Notify"));
            }
        }

        private void Send(WsSession session, string payload)
        {
            if (session.Socket.State != WebSocketState.Open) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                // SendAsync 串行化由 ASP.NET Core 保证；fire-and-forget 不阻塞消息循环
                _ = session.Socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Tools.Logger("WebSocket 发送失败: " + ex.Message);
            }
        }

        /// <summary>广播给所有连接（exceptId 可排除发起者）。</summary>
        public void Broadcast(string payload, string exceptId = null)
        {
            List<WsSession> snapshot;
            lock (_gate) snapshot = _sessions.ToList();
            foreach (var s in snapshot)
            {
                if (exceptId != null && s.Id == exceptId) continue;
                Send(s, payload);
            }
        }
    }
}

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// WebSocket 服务门面。传输层已迁移到 ASP.NET Core 原生 WebSocket
    /// （见 <see cref="WsHub"/>，与 Web 文档/REST 共用同一端口，端点 /ws），
    /// 本类保留原有广播与队列推送语义，供 Program 与 PrintQueueService 使用。
    /// </summary>
    public sealed class WebsockServerService
    {
        private readonly WebServerService _web;

        public WebsockServerService(WebServerService web)
        {
            _web = web ?? throw new ArgumentNullException(nameof(web));
        }

        public void Start()
        {
            PrintQueueService.Instance.Changed += OnQueueChanged;
            Tools.Logger("WebSocket 服务已挂载: " + _web.WebSocketEndpoint);
        }

        public void Stop()
        {
            PrintQueueService.Instance.Changed -= OnQueueChanged;
        }

        public void Broadcast(string payload) => _web.Broadcast(payload);

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

        private void OnQueueChanged(object sender, EventArgs e) => BroadcastQueueChanged();
    }
}

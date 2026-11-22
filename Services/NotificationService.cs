// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>通知事件类型。</summary>
    public enum NotifyKind { TonerLow, QuotaExceeded, JobFailed, JobHeld, QuotaWarning, System }

    /// <summary>一条通知记录（落盘供检索）。</summary>
    public class NotifyRecord
    {
        public long Ts { get; set; }
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Channels { get; set; } = "";
        public bool Ok { get; set; }
    }

    /// <summary>
    /// 通知告警服务：事件驱动 + 后台异步发送，失败静默不阻断打印主流程。
    /// 渠道：通用 Webhook（POST JSON）/ SMTP 邮件 / 钉钉群机器人 / 企业微信群机器人。
    /// 同一 kind+message 在 30 分钟内去重，避免低墨/失败刷屏。
    /// </summary>
    public static class NotificationService
    {
        private static readonly object Gate = new object();
        private static readonly Queue<NotifyRecord> Pending = new Queue<NotifyRecord>();
        private static readonly Dictionary<string, DateTime> Recent = new Dictionary<string, DateTime>();
        private static Timer _timer;
        private static bool _running;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static string HistoryPath => Path.Combine(SettingsStore.Instance.DataDir, "notifications.jsonl");

        public static void Start()
        {
            lock (Gate)
            {
                if (_running) return;
                _running = true;
                _timer = new Timer(Tick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _running = false;
                try { _timer?.Dispose(); } catch { }
                _timer = null;
            }
        }

        /// <summary>投递一条通知。同内容 30 分钟内只发一次。</summary>
        public static void Notify(NotifyKind kind, string title, string message)
        {
            var cfg = SettingsStore.Instance.Current;
            if (!cfg.NotificationsEnabled) return;
            var key = kind + "|" + (message ?? "");
            lock (Gate)
            {
                if (Recent.TryGetValue(key, out var last) && (DateTime.Now - last).TotalMinutes < 30) return;
                Recent[key] = DateTime.Now;
                if (Recent.Count > 500)
                    foreach (var k in Recent.Keys.Take(100).ToList()) Recent.Remove(k);
                Pending.Enqueue(new NotifyRecord
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Kind = kind.ToString(), Title = title ?? "", Message = message ?? ""
                });
            }
        }

        /// <summary>查询最近 N 条通知历史（新→旧）。</summary>
        public static List<NotifyRecord> History(int limit = 50)
        {
            var list = new List<NotifyRecord>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                foreach (var line in File.ReadLines(HistoryPath).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var r = JsonConvert.DeserializeObject<NotifyRecord>(line);
                    if (r != null) list.Add(r);
                    if (list.Count >= limit) break;
                }
            }
            catch (Exception ex) { Tools.Logger("通知历史读取失败: " + ex.Message); }
            return list;
        }

        private static void Tick(object state)
        {
            try
            {
                while (true)
                {
                    NotifyRecord rec;
                    lock (Gate)
                    {
                        if (Pending.Count == 0) break;
                        rec = Pending.Dequeue();
                    }
                    var channels = Dispatch(rec);
                    rec.Channels = string.Join(",", channels);
                    rec.Ok = channels.Count > 0;
                    AppendHistory(rec);
                }
            }
            catch (Exception ex) { Tools.Logger("通知派发异常: " + ex.Message); }
        }

        private static List<string> Dispatch(NotifyRecord rec)
        {
            var sent = new List<string>();
            var cfg = SettingsStore.Instance.Current;
            var text = "【" + rec.Title + "】" + Environment.NewLine + rec.Message;

            // 1) 通用 Webhook
            if (!string.IsNullOrWhiteSpace(cfg.NotifyWebhookUrl))
            {
                try
                {
                    var body = new JObject
                    {
                        ["kind"] = rec.Kind,
                        ["title"] = rec.Title,
                        ["message"] = rec.Message,
                        ["ts"] = rec.Ts,
                        ["machine"] = cfg.MachineId ?? ""
                    };
                    PostJson(cfg.NotifyWebhookUrl, body.ToString());
                    sent.Add("webhook");
                }
                catch (Exception ex) { Tools.Logger("Webhook 通知失败: " + ex.Message); }
            }

            // 2) 钉钉群机器人
            if (!string.IsNullOrWhiteSpace(cfg.DingTalkWebhook))
            {
                try
                {
                    var body = new JObject
                    {
                        ["msgtype"] = "text",
                        ["text"] = new JObject { ["content"] = text }
                    };
                    PostJson(cfg.DingTalkWebhook, body.ToString());
                    sent.Add("dingtalk");
                }
                catch (Exception ex) { Tools.Logger("钉钉通知失败: " + ex.Message); }
            }

            // 3) 企业微信群机器人
            if (!string.IsNullOrWhiteSpace(cfg.WeComWebhook))
            {
                try
                {
                    var body = new JObject
                    {
                        ["msgtype"] = "text",
                        ["text"] = new JObject { ["content"] = text }
                    };
                    PostJson(cfg.WeComWebhook, body.ToString());
                    sent.Add("wecom");
                }
                catch (Exception ex) { Tools.Logger("企微通知失败: " + ex.Message); }
            }

            // 4) SMTP 邮件
            if (!string.IsNullOrWhiteSpace(cfg.SmtpHost) && !string.IsNullOrWhiteSpace(cfg.NotifyMailTo))
            {
                try { SendMail(cfg, rec.Title, text); sent.Add("email"); }
                catch (Exception ex) { Tools.Logger("邮件通知失败: " + ex.Message); }
            }

            return sent;
        }

        private static void PostJson(string url, string json)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync(url, content).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
        }

        private static void SendMail(AppSettings cfg, string subject, string body)
        {
            using var mail = new MailMessage();
            mail.From = new MailAddress(
                string.IsNullOrWhiteSpace(cfg.SmtpFrom) ? cfg.SmtpUser : cfg.SmtpFrom,
                Brand.Title);
            foreach (var to in (cfg.NotifyMailTo ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(to)) mail.To.Add(to.Trim());
            mail.Subject = "[" + Brand.Title + "] " + subject;
            mail.Body = body;
            mail.BodyEncoding = Encoding.UTF8;
            mail.SubjectEncoding = Encoding.UTF8;
            using var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort > 0 ? cfg.SmtpPort : 25);
            smtp.EnableSsl = cfg.SmtpSsl;
            if (!string.IsNullOrWhiteSpace(cfg.SmtpUser))
                smtp.Credentials = new System.Net.NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword);
            smtp.Send(mail);
        }

        private static void AppendHistory(NotifyRecord rec)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(HistoryPath,
                        JsonConvert.SerializeObject(rec) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { Tools.Logger("通知历史写入失败: " + ex.Message); }
        }
    }
}

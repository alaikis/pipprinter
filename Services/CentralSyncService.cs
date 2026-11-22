// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using DuoAssistant.Utils;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>中心上报的一批数据。</summary>
    public class CentralPayload
    {
        public string MachineId { get; set; } = "";
        public long Ts { get; set; }
        public string Version { get; set; } = "";
        public List<JObject> Events { get; set; } = new List<JObject>();
        public List<JObject> Costs { get; set; } = new List<JObject>();
        public List<JObject> Toner { get; set; } = new List<JObject>();
    }

    /// <summary>中心已注册的客户端节点。</summary>
    public class CentralClient
    {
        public string MachineId { get; set; } = "";
        public long LastSeen { get; set; }
        public string Version { get; set; } = "";
        public int Events { get; set; }
    }

    /// <summary>
    /// 集中式多机后台（节点侧 + 中心侧最小实现）：
    /// - 节点侧：定期把本地审计/费用/墨量增量打包上报到 CentralApiUrl；失败时落盘离线缓存，下次补报。
    /// - 中心侧：POST /api/central/ingest 接收上报写入本地 central.db（同一程序可作中心）；
    ///   GET /api/central/clients 列出节点、GET /api/central/report 跨机聚合。
    /// - 未配置 CentralApiUrl 时纯本机可用，不产生任何网络流量。
    /// </summary>
    public static class CentralSyncService
    {
        private static readonly object Gate = new object();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static Timer _timer;
        private static long _lastSyncTs;
        private static string DbPath => Path.Combine(SettingsStore.Instance.DataDir, "central.db");
        private static string CachePath => Path.Combine(SettingsStore.Instance.DataDir, "central-pending.json");
        private static string ConnStr() => "Data Source=" + DbPath;

        public static void Start()
        {
            var cfg = SettingsStore.Instance.Current;
            if (string.IsNullOrWhiteSpace(cfg.CentralApiUrl)) return;
            int minutes = cfg.CentralSyncMinutes > 0 ? cfg.CentralSyncMinutes : 30;
            _lastSyncTs = LoadCursor();
            _timer = new Timer(_ => Sync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(minutes));
            Tools.Logger("集中同步已启动: " + cfg.CentralApiUrl + "，间隔 " + minutes + " 分钟");
        }

        public static void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        /// <summary>手动/定时同步：增量打包上报，成功推进游标，失败落盘缓存。</summary>
        public static string Sync()
        {
            var cfg = SettingsStore.Instance.Current;
            if (string.IsNullOrWhiteSpace(cfg.CentralApiUrl))
                return "未配置中心地址（CentralApiUrl），跳过";
            try
            {
                var events = PrintEventStore.Query("", _lastSyncTs + 1, 0, null, 1000)
                    .Select(e => JObject.FromObject(e)).ToList();
                var costs = ManagementStore.QueryCosts("", 0, 0, 1000)
                    .Where(c => c.Ts > _lastSyncTs).Select(c => JObject.FromObject(c)).ToList();
                var toner = TonerMonitorService.Query("")
                    .Select(t => JObject.FromObject(t)).ToList();

                var payload = new CentralPayload
                {
                    MachineId = cfg.MachineId ?? "",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Version = Brand.Version,
                    Events = events, Costs = costs, Toner = toner
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(cfg.CentralApiToken))
                    content.Headers.Add("X-Api-Token", cfg.CentralApiToken);
                using var resp = Http.PostAsync(cfg.CentralApiUrl.TrimEnd('/') + "/api/central/ingest", content)
                    .GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();

                _lastSyncTs = Math.Max(_lastSyncTs, payload.Ts);
                SaveCursor(_lastSyncTs);
                ClearCache();
                Tools.Logger("集中同步成功: " + events.Count + " 审计 / " + costs.Count + " 费用 / " + toner.Count + " 墨量");
                return "同步成功：" + events.Count + " 条审计、" + costs.Count + " 条费用";
            }
            catch (Exception ex)
            {
                var msg = "集中同步失败: " + ex.Message;
                Tools.Logger(msg);
                AppendCache(msg, cfg);
                return msg;
            }
        }

        /// <summary>中心侧：接收一批上报写入 central.db（供本程序作为中心时使用）。</summary>
        public static void Ingest(CentralPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.MachineId)) return;
            lock (Gate)
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                foreach (var e in payload.Events ?? new List<JObject>())
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO central_events(machine,ts,host,engine,pages,ok) VALUES($m,$t,$h,$e,$p,$o)";
                    cmd.Parameters.AddWithValue("$m", payload.MachineId);
                    cmd.Parameters.AddWithValue("$t", e["ts"]?.Value<long>() ?? 0);
                    cmd.Parameters.AddWithValue("$h", e["host"]?.ToString() ?? "");
                    cmd.Parameters.AddWithValue("$e", e["engine"]?.ToString() ?? "");
                    cmd.Parameters.AddWithValue("$p", e["pages"]?.Value<int>() ?? 0);
                    cmd.Parameters.AddWithValue("$o", e["ok"]?.Value<bool>() == true ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
                using (var meta = c.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = "INSERT OR REPLACE INTO central_clients(machine,last_seen,version,events)" +
                                       " VALUES($m,$t,$v,$n)";
                    meta.Parameters.AddWithValue("$m", payload.MachineId);
                    meta.Parameters.AddWithValue("$t", payload.Ts);
                    meta.Parameters.AddWithValue("$v", payload.Version ?? "");
                    meta.Parameters.AddWithValue("$n", payload.Events?.Count ?? 0);
                    meta.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>中心侧：列出已上报节点。</summary>
        public static List<CentralClient> Clients()
        {
            var list = new List<CentralClient>();
            try
            {
                lock (Gate)
                using (var c = Open())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT machine,last_seen,version,events FROM central_clients ORDER BY last_seen DESC";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add(new CentralClient
                        {
                            MachineId = r.GetString(0), LastSeen = r.GetInt64(1),
                            Version = r.GetString(2), Events = r.GetInt32(3)
                        });
                }
            }
            catch (Exception ex) { Tools.Logger("中心节点查询失败: " + ex.Message); }
            return list;
        }

        /// <summary>中心侧：跨机聚合统计（今日审计页数 / 失败数 / 节点数）。</summary>
        public static JObject Status()
        {
            try
            {
                lock (Gate)
                using (var c = Open())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*), SUM(pages), SUM(CASE WHEN ok=0 THEN 1 ELSE 0 END)" +
                                      " FROM central_events";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                        return new JObject
                        {
                            ["configured"] = !string.IsNullOrWhiteSpace(SettingsStore.Instance.Current.CentralApiUrl),
                            ["totalEvents"] = r.GetInt32(0),
                            ["totalPages"] = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                            ["failed"] = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                            ["clients"] = Clients().Count
                        };
                }
            }
            catch (Exception ex) { Tools.Logger("中心状态查询失败: " + ex.Message); }
            return new JObject { ["configured"] = false, ["error"] = "中心库未就绪" };
        }

        // ------------------------------------------------ 内部

        private static SqliteConnection Open()
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            SQLitePCL.Batteries.Init();
            var c = new SqliteConnection(ConnStr());
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS central_events(" +
                    "machine TEXT NOT NULL, ts INTEGER NOT NULL, host TEXT NOT NULL, engine TEXT NOT NULL," +
                    " pages INTEGER NOT NULL DEFAULT 0, ok INTEGER NOT NULL DEFAULT 1," +
                    " PRIMARY KEY(machine,ts,host,engine))";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS central_clients(" +
                    "machine TEXT PRIMARY KEY, last_seen INTEGER NOT NULL, version TEXT NOT NULL DEFAULT ''," +
                    " events INTEGER NOT NULL DEFAULT 0)";
                cmd.ExecuteNonQuery();
            }
            return c;
        }

        private static long LoadCursor()
        {
            try { return long.TryParse(File.ReadAllText(CachePath + ".cursor"), out var v) ? v : 0; }
            catch { return 0; }
        }

        private static void SaveCursor(long ts)
        {
            try { File.WriteAllText(CachePath + ".cursor", ts.ToString()); } catch { }
        }

        private static void AppendCache(string msg, AppSettings cfg)
        {
            try
            {
                File.AppendAllText(CachePath, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine);
            }
            catch { }
            NotificationService.Notify(NotifyKind.System, "集中同步失败", msg);
        }

        private static void ClearCache()
        {
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }
    }
}

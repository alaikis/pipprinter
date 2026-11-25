// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 一台打印机的「画像」：由定时扫描归一化产出，供反向路由引擎按 在线/能力/负载 选机。
    /// 与 LanPrinterScanner 的实时发现结果不同，这是幂等 upsert 的实体画像（按稳定 key），
    /// 过期用 lastSeenAt + ProfileTtlSeconds 衰减为离线，不维护「历史观测流水」。
    /// </summary>
    public sealed class PrinterProfile
    {
        public string key { get; set; } = "";          // 稳定键：本机=名称；网络=名称|地址:端口
        public string name { get; set; } = "";
        public string type { get; set; } = "";          // local | network | virtual
        public string protocol { get; set; } = "";      // ipp | ipps | raw | wsd | driver
        public string transport { get; set; } = "";     // usb | ip | share | virtual
        public string address { get; set; } = "";
        public int port { get; set; }
        public string model { get; set; } = "";
        public string capabilities { get; set; } = "";  // JSON 数组: ["duplex","color","barcode",...]
        public bool online { get; set; }
        public int load { get; set; }                   // 该打印机队列的 pending+printing
        public long lastSeenAt { get; set; }            // Unix 秒
        public long firstSeenAt { get; set; }
        public string note { get; set; } = "";
    }

    /// <summary>
    /// 定时扫描 + 打印机画像库（反向路由引擎的数据底座）。
    /// 每 ScanIntervalMinutes 分钟跑一次 LanPrinterScanner + PrinterService.GetInstalled，
    /// 归一化成 PrinterProfile，按 key upsert 落 SQLite（printer_profiles 表），
    /// 内存维护一份带 TTL 的快照供路由引擎实时读；与上轮 diff 出 新增/消失/状态翻转。
    /// 纯只读发现（不建连接、不装驱动、不改系统）；任何一路异常都不影响主打印链路。
    /// </summary>
    public sealed class PrinterDiscoveryService
    {
        private static readonly object Gate = new object();
        private static System.Threading.Timer _timer;
        private static volatile List<PrinterProfile> _snapshot = new List<PrinterProfile>();
        private static long _lastRun;
        private static Action<string, object> _push;   // 由 Program 在 Web 启动后注入

        /// <summary>注入事件推送（WS Broadcast）；未注入时状态变化只记日志，不阻断。</summary>
        public static void SetPushHandler(Action<string, object> push) { _push = push; }

        /// <summary>当前内存画像快照（按 key 索引，含过期衰减后的在线状态）。</summary>
        public static IReadOnlyList<PrinterProfile> Snapshot
        {
            get { lock (Gate) return _snapshot.ToList(); }
        }

        /// <summary>最近一次扫描时间（Unix 秒）。</summary>
        public static long LastRun { get { return _lastRun; } }

        private static string DbPath => Path.Combine(SettingsStore.Instance.DataDir, "litchio.db");

        /// <summary>启动定时扫描（间隔来自配置 AutoScanPrinters / ScanIntervalMinutes；0=关）。</summary>
        public static void Start()
        {
            try
            {
                var cfg = SettingsStore.Instance.Current;
                if (!cfg.AutoScanPrinters) return;
                int minutes = cfg.ScanIntervalMinutes;
                if (minutes <= 0) minutes = 5;
                lock (Gate)
                {
                    if (_timer != null) return;
                    _timer = new System.Threading.Timer(_ =>
                    {
                        try { RunOnce(); }
                        catch (Exception ex) { Tools.Logger("打印机定时扫描失败: " + ex.Message); }
                    }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(minutes));
                }
                Tools.Logger("打印机定时扫描已启动，间隔 " + minutes + " 分钟");
            }
            catch (Exception ex)
            {
                Tools.Logger("打印机定时扫描启动失败: " + ex.Message);
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>立即跑一次扫描（也可由 GetPrinterProfiles 命令手动触发）。返回本轮画像数。</summary>
        public static int RunOnce()
        {
            var cfg = SettingsStore.Instance.Current;
            int methods = cfg.ScanMethods;
            var set = new HashSet<string>();
            if ((methods & 0x1) != 0) set.Add("installed");
            if ((methods & 0x2) != 0) set.Add("ssdp");
            if ((methods & 0x4) != 0) set.Add("mdns");
            if ((methods & 0x8) != 0) set.Add("active");

            List<PrinterProfile> fresh = new List<PrinterProfile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            try
            {
                // ① 本机已安装打印机（本地 + 已添加的网络）
                var installed = new PrinterService().GetInstalled();
                var def = new PrinterService().GetDefault();
                foreach (var p in installed)
                {
                    string key = "LOCAL:" + p.Name;
                    if (!seen.Add(key)) continue;
                    fresh.Add(new PrinterProfile
                    {
                        key = key,
                        name = p.Name,
                        type = IsVirtualPrinter(p) ? "virtual" : "local",
                        protocol = "driver",
                        transport = IsVirtualPrinter(p) ? "virtual" : "usb",
                        model = p.Driver,
                        online = p.IsValid,
                        load = QueueLoadFor(p.Name),
                        lastSeenAt = now,
                        firstSeenAt = now,
                        capabilities = "[]"
                    });
                }

                // ② 局域网实时发现（SSDP/WSD + mDNS）——只取尚未安装的「新」设备
                if (set.Count > 0)
                {
                    var scan = new LanPrinterScanner().Scan(1500, set, "", 512);
                    foreach (var d in scan.discovered)
                    {
                        string key = "NET:" + (d.name ?? "") + "|" + d.address + ":" + d.port;
                        if (!seen.Add(key)) continue;
                        fresh.Add(new PrinterProfile
                        {
                            key = key,
                            name = d.name,
                            type = "network",
                            protocol = d.protocol ?? "raw",
                            transport = "ip",
                            address = d.address,
                            port = d.port,
                            model = d.model,
                            online = true,
                            load = 0,
                            lastSeenAt = now,
                            firstSeenAt = now,
                            capabilities = "[]"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("打印机发现异常（继续）: " + ex.Message);
            }

            // 能力推断：为每台画像填充 capabilities（本机机名关键词 + 网络机 SNMP 墨量）。
            foreach (var fp in fresh)
            {
                var caps = TonerMonitorService.DetectCapabilities(fp.address ?? "", fp.name ?? "");
                if (caps != null && caps.Length > 0)
                    fp.capabilities = Newtonsoft.Json.JsonConvert.SerializeObject(caps);
            }
            Publish(fresh, now);
            return fresh.Count;
        }

        /// <summary>把本轮发现发布到内存快照 + 落库，并 diff 出 新增/消失/翻转 事件。</summary>
        private static void Publish(List<PrinterProfile> fresh, long now)
        {
            int ttl = SettingsStore.Instance.Current.ProfileTtlSeconds;
            if (ttl <= 0) ttl = 300;

            // 旧快照里本轮没再出现的 → 按 TTL 衰减在线状态（不删，保留 lastSeenAt）
            var oldMap = new Dictionary<string, PrinterProfile>(StringComparer.Ordinal);
            lock (Gate)
            {
                foreach (var p in _snapshot) oldMap[p.key] = p;
            }
            var newMap = new Dictionary<string, PrinterProfile>(StringComparer.Ordinal);
            foreach (var p in fresh)
            {
                if (oldMap.TryGetValue(p.key, out var old))
                {
                    p.firstSeenAt = old.firstSeenAt;
                    if (old.online && !p.online)
                        RaiseStatus(p.key, "offline");
                }
                else
                {
                    p.firstSeenAt = now;
                    RaiseStatus(p.key, "discovered");
                }
                newMap[p.key] = p;
            }
            foreach (var kv in oldMap)
            {
                if (!newMap.ContainsKey(kv.Key))
                {
                    if (kv.Value.online) RaiseStatus(kv.Key, "lost");
                    newMap[kv.Key] = kv.Value; // 保留实体，标记离线
                    kv.Value.online = false;
                    kv.Value.note = "expired";
                }
            }

            lock (Gate)
            {
                _snapshot = newMap.Values.ToList();
                _lastRun = now;
            }
            Persist(newMap.Values.ToList());
        }

        /// <summary>查询某本机打印机的当前队列负载（pending+printing）。</summary>
        private static int QueueLoadFor(string printerName)
        {
            try
            {
                var s = (dynamic)PrintQueueService.Instance.Summary();
                return (int)((long)s.pending + (long)s.printing);
            }
            catch { return 0; }
        }

        private static bool IsVirtualPrinter(PrinterInfo p)
        {
            string n = (p.Name ?? "").ToLowerInvariant();
            return n.Contains("pdf") || n.Contains("wps") || n.Contains("xps") || n.Contains("virtual");
        }

        private static void RaiseStatus(string key, string kind)
        {
            var payload = new { key, kind, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            var p = _push;
            if (p != null)
            {
                try { p("printer." + kind, payload); }
                catch (Exception ex) { Tools.Logger("打印机状态事件推送失败: " + ex.Message); }
            }
            else
            {
                Tools.Logger("打印机状态变化: " + kind + " " + key);
            }
        }

        private static void Persist(List<PrinterProfile> list)
        {
            try
            {
                lock (Gate)
                {
                    var dir = Path.GetDirectoryName(DbPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Batteries.Init();
                    using var c = new SqliteConnection("Data Source=" + DbPath);
                    c.Open();
                    Exec(c, "CREATE TABLE IF NOT EXISTS printer_profiles(" +
                             "  key TEXT PRIMARY KEY," +
                             "  name TEXT NOT NULL DEFAULT ''," +
                             "  type TEXT NOT NULL DEFAULT ''," +
                             "  protocol TEXT NOT NULL DEFAULT ''," +
                             "  transport TEXT NOT NULL DEFAULT ''," +
                             "  address TEXT NOT NULL DEFAULT ''," +
                             "  port INTEGER NOT NULL DEFAULT 0," +
                             "  model TEXT NOT NULL DEFAULT ''," +
                             "  capabilities TEXT NOT NULL DEFAULT '[]'," +
                             "  online INTEGER NOT NULL DEFAULT 0," +
                             "  last_seen_at INTEGER NOT NULL DEFAULT 0," +
                             "  first_seen_at INTEGER NOT NULL DEFAULT 0," +
                             "  note TEXT NOT NULL DEFAULT '')");
                    var tx = c.BeginTransaction();
                    foreach (var p in list)
                    {
                        var cmd = c.CreateCommand();
                        cmd.CommandText = "INSERT INTO printer_profiles(" +
                                          "key,name,type,protocol,transport,address,port,model,capabilities,online,last_seen_at,first_seen_at,note)" +
                                          " VALUES($k,$n,$ty,$pr,$tr,$a,$po,$m,$cap,$on,$l,$f,$no)" +
                                          " ON CONFLICT(key) DO UPDATE SET name=excluded.name, type=excluded.type," +
                                          " protocol=excluded.protocol, transport=excluded.transport, address=excluded.address," +
                                          " port=excluded.port, model=excluded.model, online=excluded.online," +
                                          " last_seen_at=excluded.last_seen_at, note=excluded.note";
                        cmd.Parameters.AddWithValue("$k", p.key);
                        cmd.Parameters.AddWithValue("$n", p.name ?? "");
                        cmd.Parameters.AddWithValue("$ty", p.type ?? "");
                        cmd.Parameters.AddWithValue("$pr", p.protocol ?? "");
                        cmd.Parameters.AddWithValue("$tr", p.transport ?? "");
                        cmd.Parameters.AddWithValue("$a", p.address ?? "");
                        cmd.Parameters.AddWithValue("$po", p.port);
                        cmd.Parameters.AddWithValue("$m", p.model ?? "");
                        cmd.Parameters.AddWithValue("$cap", p.capabilities ?? "[]");
                        cmd.Parameters.AddWithValue("$on", p.online ? 1 : 0);
                        cmd.Parameters.AddWithValue("$l", p.lastSeenAt);
                        cmd.Parameters.AddWithValue("$f", p.firstSeenAt);
                        cmd.Parameters.AddWithValue("$no", p.note ?? "");
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("打印机画像落库失败: " + ex.Message);
            }
        }

        private static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>读画像（供 GetPrinterProfiles 命令 / 路由引擎用）。includeOffline=true 返回全部实体。</summary>
        public static List<PrinterProfile> ReadProfiles(bool includeOffline = true)
        {
            lock (Gate)
            {
                if (_snapshot.Count == 0)
                {
                    // 冷启动未跑过：从库读一次
                    try
                    {
                        var dir = Path.GetDirectoryName(DbPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        Batteries.Init();
                        using var c = new SqliteConnection("Data Source=" + DbPath);
                        c.Open();
                        Exec(c, "CREATE TABLE IF NOT EXISTS printer_profiles(" +
                                 "  key TEXT PRIMARY KEY, name TEXT, type TEXT, protocol TEXT, transport TEXT," +
                                 "  address TEXT, port INTEGER, model TEXT, capabilities TEXT, online INTEGER," +
                                 "  last_seen_at INTEGER, first_seen_at INTEGER, note TEXT)");
                        var cmd = c.CreateCommand();
                        cmd.CommandText = "SELECT key,name,type,protocol,transport,address,port,model,capabilities,online,last_seen_at,first_seen_at,note FROM printer_profiles";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            _snapshot.Add(new PrinterProfile
                            {
                                key = r.GetString(0), name = r.GetString(1), type = r.GetString(2),
                                protocol = r.GetString(3), transport = r.GetString(4), address = r.GetString(5),
                                port = r.GetInt32(6), model = r.GetString(7),
                                capabilities = r.GetString(8), online = r.GetInt32(9) == 1,
                                lastSeenAt = r.GetInt64(10), firstSeenAt = r.GetInt64(11), note = r.GetString(12)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger("打印机画像读库失败: " + ex.Message);
                    }
                }
                return _snapshot.ToList();
            }
        }

        /// <summary>供路由引擎：取当前在线且能力匹配的画像（按 load 升序）。</summary>
        public static List<PrinterProfile> OnlineCandidates(string requiredCapability = null)
        {
            var all = ReadProfiles(true);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int ttl = SettingsStore.Instance.Current.ProfileTtlSeconds;
            if (ttl <= 0) ttl = 300;
            var list = new List<PrinterProfile>();
            foreach (var p in all)
            {
                bool stillOnline = p.online && (now - p.lastSeenAt) <= ttl;
                if (!stillOnline) continue;
                if (!string.IsNullOrEmpty(requiredCapability))
                {
                    try
                    {
                        var caps = Newtonsoft.Json.Linq.JArray.Parse(p.capabilities ?? "[]");
                        if (!caps.Any(x => string.Equals((string)x, requiredCapability, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    catch { /* 解析失败视为无该能力 */ }
                }
                var p2 = p;
                p2.online = true;
                list.Add(p2);
            }
            list = list.OrderBy(p => p.load).ToList();
            return list;
        }
    }
}

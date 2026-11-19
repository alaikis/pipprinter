// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuoAssistant.Utils;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DuoAssistant.Services
{
    /// <summary>一条打印审计流水（网络直发 / 本机打印）。</summary>
    public class PrintEventRow
    {
        public long Id { get; set; }
        /// <summary>发生时间（Unix 秒）。</summary>
        public long Ts { get; set; }
        /// <summary>目标：host:port 或本机打印机名。</summary>
        public string Host { get; set; } = "";
        /// <summary>发送通道（ipp / raw / RAW-TCP / GDI / Sumatra …）。</summary>
        public string Engine { get; set; } = "";
        /// <summary>发送字节数（本机驱动管线可能为 0）。</summary>
        public int Bytes { get; set; }
        public int Copies { get; set; } = 1;
        public bool Ok { get; set; }
        /// <summary>结果说明 / 失败原因。</summary>
        public string Detail { get; set; } = "";
        /// <summary>记录该事件的本机工位机身份（MachineId），多机场景对账用。</summary>
        public string Machine { get; set; } = "";
    }

    /// <summary>
    /// 打印审计流水：本地 SQLite（数据目录/litchio.db）记录网络直发/打印事件，
    /// 供大型物流中心按 host/时间/成败检索、定期归档，控制库体积。
    /// 仅记录本机工位机自身发起的打印，不上传、不外发（外发走后续 AO 上行能力，独立演进）。
    /// </summary>
    public static class PrintEventStore
    {
        private static readonly object Gate = new object();
        private static string DbPath => Path.Combine(SettingsStore.Instance.DataDir, "litchio.db");

        private static string ConnStr() => "Data Source=" + DbPath;

        /// <summary>记录一条打印事件（调用方应在打印主流程完成后调用；失败静默，不影响打印）。</summary>
        public static long Record(string host, string engine, int bytes, int copies, bool ok, string detail)
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var machine = SafeMachine();
                lock (Gate)
                using (var c = Open())
                {
                    var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO print_events(ts, host, engine, bytes, copies, ok, detail, machine)" +
                                      " VALUES($ts,$host,$engine,$bytes,$copies,$ok,$detail,$machine)";
                    cmd.Parameters.AddWithValue("$ts", ts);
                    cmd.Parameters.AddWithValue("$host", host ?? "");
                    cmd.Parameters.AddWithValue("$engine", engine ?? "");
                    cmd.Parameters.AddWithValue("$bytes", bytes);
                    cmd.Parameters.AddWithValue("$copies", copies);
                    cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
                    cmd.Parameters.AddWithValue("$detail", detail ?? "");
                    cmd.Parameters.AddWithValue("$machine", machine);
                    cmd.ExecuteNonQuery();
                }
                return ts;
            }
            catch (Exception ex)
            {
                Tools.Logger("打印审计写入失败: " + ex.Message);
                return 0;
            }
        }

        /// <summary>按 host / 时间范围 / 成败检索流水（倒序，最新在前）。</summary>
        public static List<PrintEventRow> Query(string host, long fromTs, long toTs, bool? ok, int limit)
        {
            limit = Math.Max(1, Math.Min(1000, limit));
            var list = new List<PrintEventRow>();
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    var sb = new System.Text.StringBuilder("SELECT id,ts,host,engine,bytes,copies,ok,detail,machine FROM print_events WHERE 1=1");
                    var cmd = c.CreateCommand();
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        sb.Append(" AND host = $host");
                        cmd.Parameters.AddWithValue("$host", host);
                    }
                    if (fromTs > 0) { sb.Append(" AND ts >= $fromTs"); cmd.Parameters.AddWithValue("$fromTs", fromTs); }
                    if (toTs > 0) { sb.Append(" AND ts <= $toTs"); cmd.Parameters.AddWithValue("$toTs", toTs); }
                    if (ok.HasValue) { sb.Append(" AND ok = $ok"); cmd.Parameters.AddWithValue("$ok", ok.Value ? 1 : 0); }
                    sb.Append(" ORDER BY ts DESC, id DESC LIMIT $limit");
                    cmd.Parameters.AddWithValue("$limit", limit);
                    cmd.CommandText = sb.ToString();
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new PrintEventRow
                            {
                                Id = r.GetInt64(0),
                                Ts = r.GetInt64(1),
                                Host = r.GetString(2) ?? "",
                                Engine = r.GetString(3) ?? "",
                                Bytes = r.GetInt32(4),
                                Copies = r.GetInt32(5),
                                Ok = r.GetInt64(6) == 1,
                                Detail = r.GetString(7) ?? "",
                                Machine = r.GetString(8) ?? ""
                            });
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("打印审计查询失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>归档：删除超过 keepDays 天的旧流水；keepDays=0 清空全部。返回删除条数。</summary>
        public static int Archive(int keepDays)
        {
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    var cmd = c.CreateCommand();
                    if (keepDays <= 0)
                    {
                        cmd.CommandText = "DELETE FROM print_events";
                    }
                    else
                    {
                        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ((long)keepDays * 86400);
                        cmd.CommandText = "DELETE FROM print_events WHERE ts < $cutoff";
                        cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    }
                    return cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("打印审计归档失败: " + ex.Message);
                return -1;
            }
        }

        // ------------------------------------------------ 内部

        private static SqliteConnection Open()
        {
            EnsureDir();
            // 确保原生 SQLite 库可用（SQLitePCLRaw 提供 native；Batteries.Init 幂等，可重复调用）
            SQLitePCL.Batteries.Init();
            var c = new SqliteConnection(ConnStr());
            c.Open();
            // 开库时确保表存在
            Exec(c,
                "CREATE TABLE IF NOT EXISTS print_events(" +
                "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "  ts INTEGER NOT NULL," +
                "  host TEXT NOT NULL," +
                "  engine TEXT NOT NULL," +
                "  bytes INTEGER NOT NULL DEFAULT 0," +
                "  copies INTEGER NOT NULL DEFAULT 1," +
                "  ok INTEGER NOT NULL DEFAULT 1," +
                "  detail TEXT NOT NULL DEFAULT ''," +
                "  machine TEXT NOT NULL DEFAULT '')");
            Exec(c, "CREATE INDEX IF NOT EXISTS idx_events_ts ON print_events(ts)");
            Exec(c, "CREATE INDEX IF NOT EXISTS idx_events_host ON print_events(host)");
            // WAL 更适合「高频写 + 偶发读」的日志型表
            Exec(c, "PRAGMA journal_mode=WAL");
            Exec(c, "PRAGMA synchronous=NORMAL");
            return c;
        }

        /// <summary>在已打开连接上执行一段 SQL（无参数）。</summary>
        private static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void EnsureDir()
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static string SafeMachine()
        {
            try { return SettingsStore.Instance.Current.MachineId ?? ""; }
            catch { return ""; }
        }
    }
}

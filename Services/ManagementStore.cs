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
    /// <summary>用户配额行。</summary>
    public class QuotaRow
    {
        public string UserId { get; set; } = "";
        public int MonthlyLimit { get; set; }          // 0=不限
        public int ColorLimit { get; set; }            // 0=不限
        public int UsedPages { get; set; }
        public int UsedColorPages { get; set; }
        public decimal Spent { get; set; }
        public long PeriodStart { get; set; }          // Unix 秒，月起始
    }

    /// <summary>费用流水行。</summary>
    public class CostRow
    {
        public long Id { get; set; }
        public long Ts { get; set; }
        public string UserId { get; set; } = "";
        public string Printer { get; set; } = "";
        public string JobId { get; set; } = "";
        public int Pages { get; set; }
        public bool Color { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>打印策略行（按用户/打印机/时段匹配，Enqueue 前评估）。</summary>
    public class PolicyRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string UserId { get; set; } = "";       // 空=所有用户
        public string Printer { get; set; } = "";      // 空=所有打印机
        public int StartHour { get; set; } = 0;        // 0-23
        public int EndHour { get; set; } = 24;         // 0-24，24=全天
        public bool ForceDuplex { get; set; }
        public bool ForceBW { get; set; }
        public bool DenyColor { get; set; }
        public int MaxCopies { get; set; }             // 0=不限
        public bool Enabled { get; set; } = true;
    }

    /// <summary>报表聚合结果（按维度分组）。</summary>
    public class ReportRow
    {
        public string Key { get; set; } = "";
        public int Jobs { get; set; }
        public int Pages { get; set; }
        public int ColorPages { get; set; }
        public int Failed { get; set; }
        public long Bytes { get; set; }
        public decimal Cost { get; set; }
    }

    /// <summary>
    /// 打印管理统一存储：用户配额、费用流水、策略、存档索引、报表聚合。
    /// 与 PrintEventStore 共用同一个 SQLite 文件（litchio.db），表结构互不耦合。
    /// 全部操作失败静默（管理数据不应阻断打印主流程）。
    /// </summary>
    public static class ManagementStore
    {
        private static readonly object Gate = new object();
        private static string DbPath => Path.Combine(SettingsStore.Instance.DataDir, "litchio.db");
        private static string ConnStr() => "Data Source=" + DbPath;

        // ------------------------------------------------ 配额

        public static QuotaRow GetQuota(string userId)
        {
            userId = userId ?? "";
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    EnsurePeriod(c, userId);
                    var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT user_id,monthly_limit,color_limit,used_pages,used_color,spent,period_start" +
                                      " FROM quotas WHERE user_id=$u";
                    cmd.Parameters.AddWithValue("$u", userId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                        return new QuotaRow
                        {
                            UserId = r.GetString(0),
                            MonthlyLimit = r.GetInt32(1),
                            ColorLimit = r.GetInt32(2),
                            UsedPages = r.GetInt32(3),
                            UsedColorPages = r.GetInt32(4),
                            Spent = Convert.ToDecimal(r.GetDouble(5)),
                            PeriodStart = r.GetInt64(6)
                        };
                }
            }
            catch (Exception ex) { Tools.Logger("配额查询失败: " + ex.Message); }
            return new QuotaRow { UserId = userId };
        }

        /// <summary>设置配额。monthlyLimit/colorLimit=0 表示不限。</summary>
        public static bool SetQuota(string userId, int monthlyLimit, int colorLimit)
        {
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    EnsurePeriod(c, userId);
                    var cmd = c.CreateCommand();
                    cmd.CommandText = "UPDATE quotas SET monthly_limit=$m, color_limit=$cl WHERE user_id=$u";
                    cmd.Parameters.AddWithValue("$m", Math.Max(0, monthlyLimit));
                    cmd.Parameters.AddWithValue("$cl", Math.Max(0, colorLimit));
                    cmd.Parameters.AddWithValue("$u", userId ?? "");
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex) { Tools.Logger("配额设置失败: " + ex.Message); return false; }
        }

        /// <summary>检查是否允许打印（配额未超）。返回 null 表示允许，否则返回拒绝原因。</summary>
        public static string CheckQuota(string userId, int pages, bool color)
        {
            if (pages <= 0) return null;
            try
            {
                var q = GetQuota(userId);
                if (q.MonthlyLimit > 0 && q.UsedPages + pages > q.MonthlyLimit)
                    return "用户 " + (string.IsNullOrEmpty(userId) ? "default" : userId) +
                           " 本月页数配额不足：已用 " + q.UsedPages + "，限额 " + q.MonthlyLimit;
                if (color && q.ColorLimit > 0 && q.UsedColorPages + pages > q.ColorLimit)
                    return "用户 " + (string.IsNullOrEmpty(userId) ? "default" : userId) +
                           " 本月彩色配额不足：已用 " + q.UsedColorPages + "，限额 " + q.ColorLimit;
            }
            catch (Exception ex) { Tools.Logger("配额检查失败(放行): " + ex.Message); }
            return null;
        }

        /// <summary>打印成功后记账（页数 + 彩色页数 + 金额）。</summary>
        public static void Charge(string userId, string printer, string jobId, int pages, bool color, decimal amount)
        {
            if (pages <= 0) return;
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    EnsurePeriod(c, userId);
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var ins = c.CreateCommand();
                    ins.CommandText = "INSERT INTO print_costs(ts,user_id,printer,job_id,pages,color,amount)" +
                                      " VALUES($ts,$u,$p,$j,$pg,$c,$a)";
                    ins.Parameters.AddWithValue("$ts", ts);
                    ins.Parameters.AddWithValue("$u", userId ?? "");
                    ins.Parameters.AddWithValue("$p", printer ?? "");
                    ins.Parameters.AddWithValue("$j", jobId ?? "");
                    ins.Parameters.AddWithValue("$pg", pages);
                    ins.Parameters.AddWithValue("$c", color ? 1 : 0);
                    ins.Parameters.AddWithValue("$a", (double)amount);
                    ins.ExecuteNonQuery();

                    var upd = c.CreateCommand();
                    upd.CommandText = "UPDATE quotas SET used_pages=used_pages+$pg, " +
                                      "used_color=used_color+$clr, spent=spent+$a WHERE user_id=$u";
                    upd.Parameters.AddWithValue("$pg", pages);
                    upd.Parameters.AddWithValue("$clr", color ? pages : 0);
                    upd.Parameters.AddWithValue("$a", (double)amount);
                    upd.Parameters.AddWithValue("$u", userId ?? "");
                    upd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { Tools.Logger("费用记账失败: " + ex.Message); }
        }

        /// <summary>新月份自动重置用量（保留额度配置与历史费用流水）。</summary>
        private static void EnsurePeriod(SqliteConnection c, string userId)
        {
            var now = DateTime.Now;
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
            var ts = new DateTimeOffset(start).ToUnixTimeSeconds();
            var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO quotas(user_id,monthly_limit,color_limit,used_pages,used_color,spent,period_start)" +
                              " VALUES($u,0,0,0,0,0,$ts)";
            cmd.Parameters.AddWithValue("$u", userId ?? "");
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();

            var reset = c.CreateCommand();
            reset.CommandText = "UPDATE quotas SET used_pages=0, used_color=0, spent=0, period_start=$ts" +
                                " WHERE user_id=$u AND period_start < $ts";
            reset.Parameters.AddWithValue("$ts", ts);
            reset.Parameters.AddWithValue("$u", userId ?? "");
            reset.ExecuteNonQuery();
        }

        // ------------------------------------------------ 费用流水

        public static List<CostRow> QueryCosts(string userId, long fromTs, long toTs, int limit)
        {
            limit = Math.Max(1, Math.Min(1000, limit));
            var list = new List<CostRow>();
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    var sb = new System.Text.StringBuilder(
                        "SELECT id,ts,user_id,printer,job_id,pages,color,amount FROM print_costs WHERE 1=1");
                    var cmd = c.CreateCommand();
                    if (!string.IsNullOrWhiteSpace(userId))
                    { sb.Append(" AND user_id=$u"); cmd.Parameters.AddWithValue("$u", userId); }
                    if (fromTs > 0) { sb.Append(" AND ts>=$f"); cmd.Parameters.AddWithValue("$f", fromTs); }
                    if (toTs > 0) { sb.Append(" AND ts<=$t"); cmd.Parameters.AddWithValue("$t", toTs); }
                    sb.Append(" ORDER BY ts DESC LIMIT $l");
                    cmd.Parameters.AddWithValue("$l", limit);
                    cmd.CommandText = sb.ToString();
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add(new CostRow
                        {
                            Id = r.GetInt64(0), Ts = r.GetInt64(1), UserId = r.GetString(2),
                            Printer = r.GetString(3), JobId = r.GetString(4), Pages = r.GetInt32(5),
                            Color = r.GetInt64(6) == 1, Amount = Convert.ToDecimal(r.GetDouble(7))
                        });
                }
            }
            catch (Exception ex) { Tools.Logger("费用查询失败: " + ex.Message); }
            return list;
        }

        // ------------------------------------------------ 策略

        public static List<PolicyRow> ListPolicies()
        {
            var list = new List<PolicyRow>();
            try
            {
                lock (Gate)
                using (var c = Open())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT id,name,user_id,printer,start_hour,end_hour,force_duplex,force_bw,deny_color,max_copies,enabled" +
                                      " FROM policies ORDER BY id";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add(new PolicyRow
                        {
                            Id = r.GetInt64(0), Name = r.GetString(1), UserId = r.GetString(2),
                            Printer = r.GetString(3), StartHour = r.GetInt32(4), EndHour = r.GetInt32(5),
                            ForceDuplex = r.GetInt64(6) == 1, ForceBW = r.GetInt64(7) == 1,
                            DenyColor = r.GetInt64(8) == 1, MaxCopies = r.GetInt32(9),
                            Enabled = r.GetInt64(10) == 1
                        });
                }
            }
            catch (Exception ex) { Tools.Logger("策略查询失败: " + ex.Message); }
            return list;
        }

        public static bool SavePolicy(PolicyRow p)
        {
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    var cmd = c.CreateCommand();
                    if (p.Id > 0)
                    {
                        cmd.CommandText = "UPDATE policies SET name=$n,user_id=$u,printer=$p,start_hour=$sh,end_hour=$eh," +
                                          "force_duplex=$fd,force_bw=$fb,deny_color=$dc,max_copies=$mc,enabled=$en WHERE id=$id";
                        cmd.Parameters.AddWithValue("$id", p.Id);
                    }
                    else
                    {
                        cmd.CommandText = "INSERT INTO policies(name,user_id,printer,start_hour,end_hour,force_duplex,force_bw,deny_color,max_copies,enabled)" +
                                          " VALUES($n,$u,$p,$sh,$eh,$fd,$fb,$dc,$mc,$en)";
                    }
                    cmd.Parameters.AddWithValue("$n", p.Name ?? "");
                    cmd.Parameters.AddWithValue("$u", p.UserId ?? "");
                    cmd.Parameters.AddWithValue("$p", p.Printer ?? "");
                    cmd.Parameters.AddWithValue("$sh", p.StartHour);
                    cmd.Parameters.AddWithValue("$eh", p.EndHour);
                    cmd.Parameters.AddWithValue("$fd", p.ForceDuplex ? 1 : 0);
                    cmd.Parameters.AddWithValue("$fb", p.ForceBW ? 1 : 0);
                    cmd.Parameters.AddWithValue("$dc", p.DenyColor ? 1 : 0);
                    cmd.Parameters.AddWithValue("$mc", p.MaxCopies);
                    cmd.Parameters.AddWithValue("$en", p.Enabled ? 1 : 0);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex) { Tools.Logger("策略保存失败: " + ex.Message); return false; }
        }

        public static bool DeletePolicy(long id)
        {
            try
            {
                lock (Gate)
                using (var c = Open())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM policies WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex) { Tools.Logger("策略删除失败: " + ex.Message); return false; }
        }

        // ------------------------------------------------ 报表聚合

        /// <summary>按维度聚合报表：groupBy = printer | user | hour | color | engine。</summary>
        public static List<ReportRow> QueryReport(long fromTs, long toTs, string groupBy)
        {
            var list = new List<ReportRow>();
            string expr;
            switch ((groupBy ?? "printer").ToLowerInvariant())
            {
                case "user": expr = "host"; break;      // print_events 无 user 列，用户维度走 print_costs
                case "hour": expr = "strftime('%H', ts, 'unixepoch', 'localtime')"; break;
                case "color": expr = "CASE WHEN instr(lower(detail),'color')>0 OR instr(lower(detail),'彩色')>0 THEN 'color' ELSE 'bw' END"; break;
                case "engine": expr = "engine"; break;
                default: expr = "host"; break;
            }
            try
            {
                lock (Gate)
                using (var c = Open())
                {
                    var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT " + expr + " AS k, COUNT(*) AS jobs, SUM(bytes) AS bytes," +
                                      " SUM(CASE WHEN ok=1 THEN pages ELSE 0 END) AS pages," +
                                      " SUM(CASE WHEN ok=0 THEN 1 ELSE 0 END) AS fails" +
                                      " FROM print_events WHERE 1=1";
                    if (fromTs > 0) { cmd.CommandText += " AND ts>=$f"; cmd.Parameters.AddWithValue("$f", fromTs); }
                    if (toTs > 0) { cmd.CommandText += " AND ts<=$t"; cmd.Parameters.AddWithValue("$t", toTs); }
                    cmd.CommandText += " GROUP BY k ORDER BY k";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        list.Add(new ReportRow
                        {
                            Key = r.IsDBNull(0) ? "-" : r.GetString(0),
                            Jobs = r.GetInt32(1),
                            Bytes = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                            Pages = r.IsDBNull(3) ? 0 : r.GetInt32(3), // v1.01.012 起为审计流水的真实页数（旧库为 0）
                            Failed = r.GetInt32(4)
                        });
                }
                // 费用按相同维度补充（user/printer）
                if (groupBy == "user" || groupBy == "printer" || string.IsNullOrEmpty(groupBy))
                {
                    var costCmd = groupBy == "user"
                        ? "SELECT user_id, SUM(pages), SUM(CASE WHEN color=1 THEN pages ELSE 0 END), SUM(amount) FROM print_costs GROUP BY user_id"
                        : "SELECT printer, SUM(pages), SUM(CASE WHEN color=1 THEN pages ELSE 0 END), SUM(amount) FROM print_costs GROUP BY printer";
                    lock (Gate)
                    using (var c = Open())
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = costCmd;
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var key = r.GetString(0);
                            var row = list.FirstOrDefault(x => x.Key == key);
                            if (row == null) { row = new ReportRow { Key = key }; list.Add(row); }
                            row.Pages = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                            row.ColorPages = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                            row.Cost = r.IsDBNull(3) ? 0 : Convert.ToDecimal(r.GetDouble(3));
                        }
                    }
                }
            }
            catch (Exception ex) { Tools.Logger("报表聚合失败: " + ex.Message); }
            return list;
        }

        // ------------------------------------------------ 内部

        private static SqliteConnection Open()
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Batteries.Init();
            var c = new SqliteConnection(ConnStr());
            c.Open();
            Exec(c, "CREATE TABLE IF NOT EXISTS quotas(" +
                    "  user_id TEXT PRIMARY KEY," +
                    "  monthly_limit INTEGER NOT NULL DEFAULT 0," +
                    "  color_limit INTEGER NOT NULL DEFAULT 0," +
                    "  used_pages INTEGER NOT NULL DEFAULT 0," +
                    "  used_color INTEGER NOT NULL DEFAULT 0," +
                    "  spent REAL NOT NULL DEFAULT 0," +
                    "  period_start INTEGER NOT NULL DEFAULT 0)");
            Exec(c, "CREATE TABLE IF NOT EXISTS print_costs(" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  ts INTEGER NOT NULL," +
                    "  user_id TEXT NOT NULL DEFAULT ''," +
                    "  printer TEXT NOT NULL DEFAULT ''," +
                    "  job_id TEXT NOT NULL DEFAULT ''," +
                    "  pages INTEGER NOT NULL DEFAULT 0," +
                    "  color INTEGER NOT NULL DEFAULT 0," +
                    "  amount REAL NOT NULL DEFAULT 0)");
            Exec(c, "CREATE INDEX IF NOT EXISTS idx_costs_ts ON print_costs(ts)");
            Exec(c, "CREATE INDEX IF NOT EXISTS idx_costs_user ON print_costs(user_id)");
            Exec(c, "CREATE TABLE IF NOT EXISTS policies(" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  name TEXT NOT NULL DEFAULT ''," +
                    "  user_id TEXT NOT NULL DEFAULT ''," +
                    "  printer TEXT NOT NULL DEFAULT ''," +
                    "  start_hour INTEGER NOT NULL DEFAULT 0," +
                    "  end_hour INTEGER NOT NULL DEFAULT 24," +
                    "  force_duplex INTEGER NOT NULL DEFAULT 0," +
                    "  force_bw INTEGER NOT NULL DEFAULT 0," +
                    "  deny_color INTEGER NOT NULL DEFAULT 0," +
                    "  max_copies INTEGER NOT NULL DEFAULT 0," +
                    "  enabled INTEGER NOT NULL DEFAULT 1)");
            Exec(c, "PRAGMA journal_mode=WAL");
            Exec(c, "PRAGMA synchronous=NORMAL");
            return c;
        }

        private static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}

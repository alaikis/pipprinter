// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>反向路由结果：意图 → 选定的打印机（含 指定 / 自动 双模标识与调度说明）。</summary>
    public sealed class ReverseRouteResult
    {
        public string Printer { get; set; } = "";
        public string Channel { get; set; } = "Driver";
        public string Reason { get; set; } = "";
        /// <summary>true=用户指定了打印机（走 SlugResolver 原逻辑）；false=引擎自动选（反向路由）。</summary>
        public bool UserSpecified { get; set; }
        /// <summary>自动模式下选中的画像 key（便于审计）。</summary>
        public string SelectedKey { get; set; } = "";
        /// <summary>指定打印机不在线/不匹配时回退自动选，标记 true。</summary>
        public bool Fallback { get; set; }
        /// <summary>候选（打分后排序）数量，便于调用方核对。</summary>
        public int CandidateCount { get; set; }
        /// <summary>打印故障转移候选链（按优先级排序，含首选）。打印失败时依次尝试。</summary>
        public List<string> Candidates { get; set; } = new List<string>();
    }

    /// <summary>
    /// 反向动态调度引擎：把「用户意图」反向匹配到「最合适的一台打印机」。
    /// 用户侧不感知打印机类型/协议/能力细节——只需给「要打印什么」（文件 + 打印参数），
    /// 引擎按画像库的 在线 / 能力 / 负载 反向选机：
    ///   指定模式：printer 非空 → SlugResolver 原逻辑；命中在线且能力匹配即采用，
    ///             不匹配则回退自动并标 fallback。
    ///   自动模式：printer 省略 → 从 PrinterDiscoveryService.OnlineCandidates() 取候选，
    ///             按 ReverseRoutingPolicy（idle-first / round-robin / nearest）打分排序，
    ///             选第一台；全候选离线/无候选时回退系统默认打印机并标 fallback。
    /// 引擎是纯本地只读（画像库由定时扫描维护），不影响主打印链路；选出的目标
    /// 经 SlugResolver 解析成 通道 + 实际目标，复用现有 Net/Driver 管线。
    /// </summary>
    public static class ReverseRouteService
    {
        /// <summary>
        /// 反向路由入口。printer 为 null/空 走自动模式；非空走指定模式。
        /// needCapability 为调用方从任务内容推断出的能力需求（如 "color" / "duplex" / "barcode"）。
        /// </summary>
        public static ReverseRouteResult Route(string printer, string needCapability = null, int copies = 1)
        {
            var s = SettingsStore.Instance.Current;

            // ---- 指定模式 ----
            if (!string.IsNullOrWhiteSpace(printer))
            {
                var resolved = SlugResolver.Resolve(printer);
                string target = resolved.Printer ?? "";

                // 若目标在本机画像里，校验 在线 + 能力
                bool targetSpecifiedInProfiles = false;
                if (!string.IsNullOrWhiteSpace(target) && s.ReverseRoutingEnabled)
                {
                    var all = PrinterDiscoveryService.ReadProfiles(true);
                    var match = all.FirstOrDefault(x =>
                        string.Equals(x.name, target, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.key, "LOCAL:" + target, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        targetSpecifiedInProfiles = true;
                        bool stillOnline = match.online &&
                            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - match.lastSeenAt) <=
                            EffectiveTtl(s.ProfileTtlSeconds);
                        bool capOk = CapabilityOk(match.capabilities, needCapability);
                        if (stillOnline && capOk)
                        {
                            return new ReverseRouteResult
                            {
                                Printer = target,
                                Channel = resolved.Channel,
                                Reason = "指定打印机「" + target + "」在线" +
                                        (string.IsNullOrEmpty(needCapability) ? "" : "且能力匹配"),
                                UserSpecified = true,
                                SelectedKey = match.key,
                                Candidates = BuildFailoverChain(target, needCapability, s)
                            };
                        }
                        // 不在线 / 能力不匹配 → 回退自动（保留指定意图但标记 fallback）
                    }
                }

                // 目标未登记画像或不可用 → 仍采用指定目标（保持用户意图），不做二次猜测
                if (targetSpecifiedInProfiles)
                {
                    // 指定目标不可用 → 自动选机
                    var auto = AutoPick(null, needCapability, s, copies);
                    auto.UserSpecified = false;
                    auto.Fallback = true;
                    auto.Reason = "指定打印机「" + target + "」不在线/能力不匹配，自动切到 " +
                                  (auto.Printer ?? "默认") + "（fallback）";
                    return auto;
                }

                // 目标未登记画像（如裸 host:port 或别名）→ 直接用，不猜
                var bareChain = BuildFailoverChain(target, needCapability, s);
                return new ReverseRouteResult
                {
                    Printer = target,
                    Channel = resolved.Channel,
                    Reason = resolved.Detail +
                        (bareChain.Count > 1 ? "；故障转移候选 " + bareChain.Count + " 台" : ""),
                    UserSpecified = true,
                    CandidateCount = bareChain.Count,
                    Candidates = bareChain
                };
            }

            // ---- 自动模式 ----
            if (!s.ReverseRoutingEnabled)
            {
                return new ReverseRouteResult
                {
                    Printer = "",
                    Channel = "Driver",
                    Reason = "反向路由未启用，使用默认打印机",
                    UserSpecified = false,
                    Fallback = false
                };
            }
            return AutoPick(printer, needCapability, s, copies);
        }

        private static ReverseRouteResult AutoPick(string requestedPrinter, string needCapability,
            AppSettings s, int copies)
        {
            var cands = PrinterDiscoveryService.OnlineCandidates(needCapability);
            // 虚拟文档打印机（PDF/XPS 虚拟端口）不能直接接收打印，选机时优先排除；全是虚拟时才保留
            if (cands != null && cands.Count > 0)
            {
                var real = cands.Where(x => !string.Equals(x.type, "virtual", StringComparison.OrdinalIgnoreCase)).ToList();
                if (real.Count > 0) cands = real;
            }
            if (cands == null || cands.Count == 0)
            {
                return new ReverseRouteResult
                {
                    Printer = "",
                    Channel = "Driver",
                    Reason = "无在线打印机" + (string.IsNullOrEmpty(needCapability) ? "" : "（能力 " +
                              needCapability + " 匹配）") + "，回退默认",
                    UserSpecified = false,
                    Fallback = true,
                    CandidateCount = 0
                };
            }

            // 按策略排序（与故障转移候选链同一套顺序）
            var picked = OrderCandidates(cands, s);

            var best = picked.First();
            string target = ResolveProfileToTarget(best);
            var resolved = SlugResolver.Resolve(target);
            // 故障转移候选链：首选 + 其余在线候选（受 PrintFailoverMaxAttempts 限制）
            var chain = new List<string> { target };
            if (s.PrintFailoverEnabled)
            {
                int maxTry = s.PrintFailoverMaxAttempts > 0 ? s.PrintFailoverMaxAttempts : 3;
                foreach (var p in picked.Skip(1))
                {
                    if (string.Equals(p.type, "virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    if (chain.Count >= maxTry) break;
                    var t = ResolveProfileToTarget(p);
                    if (!chain.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                        chain.Add(t);
                }
            }
            return new ReverseRouteResult
            {
                Printer = target,
                Channel = resolved.Channel,
                Reason = "自动选中「" + best.name + "」" +
                        (best.type == "network" ? "（网络 " + best.address + ":" + best.port +
                         " " + best.protocol + "）" : "（本机）") +
                        "，策略=" + (s.ReverseRoutingPolicy ?? "idle-first") + "，候选=" + cands.Count,
                UserSpecified = false,
                Fallback = false,
                SelectedKey = best.key,
                CandidateCount = cands.Count,
                Candidates = chain
            };
        }

        /// <summary>
        /// 构造打印故障转移候选链：首选 + 其它在线候选（按策略排序），
        /// 数量受 PrintFailoverMaxAttempts 限制；未启用故障转移时只保留首选。
        /// </summary>
        private static List<string> BuildFailoverChain(string primary, string needCapability, AppSettings s)
        {
            var chain = new List<string>();
            if (!string.IsNullOrWhiteSpace(primary)) chain.Add(primary);
            if (!s.PrintFailoverEnabled) return chain;
            try
            {
                int maxTry = s.PrintFailoverMaxAttempts > 0 ? s.PrintFailoverMaxAttempts : 3;
                var cands = PrinterDiscoveryService.OnlineCandidates(needCapability);
                if (cands == null) return chain;
                cands = OrderCandidates(cands, s);
                foreach (var c in cands)
                {
                    if (string.Equals(c.type, "virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    if (chain.Count >= maxTry) break;
                    var t = ResolveProfileToTarget(c);
                    if (chain.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
                    chain.Add(t);
                }
            }
            catch { /* 候选链构造失败不影响首选 */ }
            return chain;
        }

        /// <summary>把画像还原成 SlugResolver 能吃的目标（本机名 / host:port）。</summary>
        private static string ResolveProfileToTarget(PrinterProfile p)
        {
            if (p.type == "network" && !string.IsNullOrEmpty(p.address))
            {
                int port = p.port > 0 ? p.port : (p.protocol == "ipps" ? 443 : 631);
                return p.address + ":" + port;
            }
            return p.name;
        }

        /// <summary>按 ReverseRoutingPolicy 对候选排序：idle-first(默认) / round-robin / nearest。</summary>
        private static List<PrinterProfile> OrderCandidates(List<PrinterProfile> cands, AppSettings s)
        {
            var policy = (s.ReverseRoutingPolicy ?? "idle-first").Trim().ToLowerInvariant();
            if (policy == "round-robin")
            {
                int idx = NextRoundRobin();
                return cands.Skip(idx % cands.Count).Concat(cands.Take(idx % cands.Count)).ToList();
            }
            if (policy == "nearest")
            {
                // nearest：本机(local) 优先，其次按负载
                return cands.OrderBy(x => x.type == "local" ? 0 : 1).ThenBy(x => x.load).ToList();
            }
            // idle-first（默认）：负载最低优先
            return cands.OrderBy(x => x.load).ThenBy(x => x.name).ToList();
        }

        private static int NextRoundRobin()
        {
            lock (_rrGate)
            {
                int v = _rrSeq;
                _rrSeq = (v + 1) % 1_000_000;
                return v;
            }
        }
        private static int _rrSeq;
        private static readonly object _rrGate = new object();

        private static int EffectiveTtl(int ttl) => ttl <= 0 ? 300 : ttl;

        private static bool CapabilityOk(string capsJson, string need)
        {
            if (string.IsNullOrEmpty(need)) return true;
            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(capsJson ?? "[]");
                return arr.Any(x => string.Equals((string)x, need, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>
        /// 从任务意图推断需要的打印机能力（反向调度引擎的「意图识别」层）。
        /// 返回 null 表示「任意在线机即可」；返回字符串表示必须具备该能力。
        /// 识别依据：显式字段（color/duplex）+ 命令类型（barcode/escpos）+ 文件类别。
        /// </summary>
        public static string InferNeed(string cmdName, string fileCategory, bool color, string duplex)
        {
            if (!string.IsNullOrEmpty(cmdName))
            {
                var c = cmdName.Trim().ToLowerInvariant();
                if (c == "printescpos" || c == "printbarcode")
                    return c == "printescpos" ? "escpos" : "barcode";
            }
            // 彩色优先（要求彩色能力）
            if (color) return "color";
            // 双面（要求 duplex 能力）
            if (!string.IsNullOrEmpty(duplex) &&
                !string.Equals(duplex, "simplex", StringComparison.OrdinalIgnoreCase))
                return "duplex";
            // 其余：任意在线机（不指定能力）
            return null;
        }
    }
}

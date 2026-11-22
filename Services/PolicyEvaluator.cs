// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using DuoAssistant.Models;

namespace DuoAssistant.Services
{
    /// <summary>策略评估结果。</summary>
    public class PolicyDecision
    {
        public bool Allowed { get; set; } = true;
        public string Reason { get; set; } = "";
        public List<string> Applied { get; set; } = new List<string>();
    }

    /// <summary>
    /// 打印策略引擎：按用户 / 打印机 / 时段匹配策略，在入队前覆写参数或直接拒绝。
    /// 无策略时完全放行（保持既有行为不变）。策略按定义顺序叠加，后定义优先。
    /// </summary>
    public static class PolicyEvaluator
    {
        /// <summary>对任务应用策略。返回 Allowed=false 时调用方应拒绝入队。</summary>
        public static PolicyDecision Apply(PrintJob job)
        {
            var result = new PolicyDecision();
            if (job == null) return result;
            var hour = DateTime.Now.Hour;
            var allPolicies = ManagementStore.ListPolicies().Where(p => p.Enabled).ToList();
            var matched = allPolicies.Where(p =>
                (string.IsNullOrWhiteSpace(p.UserId) ||
                 string.Equals(p.UserId, job.UserId, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(p.Printer) ||
                 string.Equals(p.Printer, job.Printer, StringComparison.OrdinalIgnoreCase)) &&
                InWindow(hour, p.StartHour, p.EndHour)).ToList();

            foreach (var p in matched)
            {
                if (p.DenyColor && (job.Color == null || job.Color.Value))
                {
                    job.Color = false;
                    result.Applied.Add("策略「" + p.Name + "」：彩色被禁止，已转黑白");
                }
                if (p.ForceBW && job.Color != false)
                {
                    job.Color = false;
                    result.Applied.Add("策略「" + p.Name + "」：强制黑白");
                }
                if (p.ForceDuplex && job.Duplex != "duplexlong" && job.Duplex != "duplexshort")
                {
                    job.Duplex = "duplexlong";
                    result.Applied.Add("策略「" + p.Name + "」：强制双面");
                }
                if (p.MaxCopies > 0 && job.Copies > p.MaxCopies)
                {
                    job.Copies = p.MaxCopies;
                    result.Applied.Add("策略「" + p.Name + "」：份数上限 " + p.MaxCopies);
                }
            }

            if (result.Applied.Count > 0)
                job.PolicyApplied = string.Join("；", result.Applied);
            return result;
        }

        private static bool InWindow(int hour, int start, int end)
        {
            if (start == 0 && end == 24) return true;
            if (start <= end) return hour >= start && hour < end;
            return hour >= start || hour < end; // 跨午夜窗口
        }
    }
}

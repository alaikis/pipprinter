// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DuoAssistant.Models;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 安全打印（Print Release）：任务入队时标记 releaseRequired 并挂起为 held 状态，
    /// 不出纸。用户在打印机前通过 PIN / 工卡认证后调用 Release(jobId, pin) 才进入待打印队列。
    /// PIN 用 PBKDF2-SHA256（salt + 10000 轮）存哈希，不落明文。
    /// 无 PIN 的公开任务可由任意调用方释放（配合工卡/内网鉴权的上层系统）。
    /// </summary>
    public static class SecureReleaseService
    {
        private const int Iterations = 10000;

        public static string HashPin(string pin, out string salt)
        {
            salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            return HashWithSalt(pin ?? "", salt);
        }

        private static string HashWithSalt(string pin, string salt)
        {
            var bytes = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(salt),
                Iterations, HashAlgorithmName.SHA256, 32);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>校验 PIN。pinHash 格式：salt$hash；空哈希表示无 PIN（任意释放）。</summary>
        public static bool Verify(string pinHash, string pin)
        {
            if (string.IsNullOrWhiteSpace(pinHash)) return true;
            var parts = pinHash.Split('$');
            if (parts.Length != 2) return false;
            try { return HashWithSalt(pin ?? "", parts[0]) == parts[1]; }
            catch { return false; }
        }

        /// <summary>生成 PinHash 存储值（salt$hash）。</summary>
        public static string MakePinHash(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin)) return "";
            var h = HashPin(pin, out var salt);
            return salt + "$" + h;
        }

        /// <summary>列出所有挂起等待认证的任务。</summary>
        public static List<PrintJob> HeldJobs() =>
            PrintQueueService.Instance.GetAll()
                .Where(x => x.Status == JobStatus.Held)
                .OrderBy(x => x.EnqueuedAt).ToList();

        /// <summary>
        /// 认证释放一个挂起任务。返回 (ok, message)。
        /// 成功时把状态从 held 改为 pending 并唤醒工作线程。
        /// </summary>
        public static (bool, string) Release(string jobId, string pin, string releasedBy)
        {
            var jobs = PrintQueueService.Instance.GetAll();
            var job = jobs.FirstOrDefault(x => x.Id == jobId);
            if (job == null) return (false, "任务不存在: " + jobId);
            if (job.Status != JobStatus.Held) return (false, "任务未处于等待认证状态（当前: " + job.Status + "）");
            if (!Verify(job.PinHash, pin)) return (false, "PIN 错误");

            // 通过队列服务改写状态（内部持锁）
            var ok = PrintQueueService.Instance.ReleaseHeld(jobId, releasedBy ?? "pin");
            if (!ok) return (false, "释放失败（任务状态已变化）");
            PrintEventStore.Record("release:" + jobId, "SecureRelease", 0, 1, true,
                "安全打印释放，释放人: " + (releasedBy ?? "pin"));
            return (true, "任务已释放，进入打印队列");
        }
    }
}

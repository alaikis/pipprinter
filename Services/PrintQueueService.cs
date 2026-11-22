// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Models;
using DuoAssistant.Utils;
using Newtonsoft.Json;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 打印队列：内存列表 + data/queue.json 持久化，后台单线程顺序消费。
    /// 崩溃恢复时把残留的 printing 状态重置为 pending。
    /// </summary>
    public sealed class PrintQueueService
    {
        private static readonly Lazy<PrintQueueService> Lazy =
            new Lazy<PrintQueueService>(() => new PrintQueueService(), true);

        private readonly object _gate = new object();
        private readonly List<PrintJob> _jobs = new List<PrintJob>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ExtendedPrintService _printService = new ExtendedPrintService();

        public static PrintQueueService Instance => Lazy.Value;

        /// <summary>队列发生变化（工作线程触发，UI 需自行封送）。</summary>
        public event EventHandler Changed;

        public DateTime StartedAt { get; } = DateTime.Now;
        public bool IsWorkerRunning { get; private set; }

        private PrintQueueService()
        {
            Load();
            Task.Run(WorkerLoop);
        }

        public List<PrintJob> GetAll()
        {
            lock (_gate) return _jobs.Select(x => x.Clone()).ToList();
        }

        public object Summary()
        {
            lock (_gate)
            {
                return new
                {
                    total = _jobs.Count,
                    pending = _jobs.Count(x => x.Status == JobStatus.Pending),
                    held = _jobs.Count(x => x.Status == JobStatus.Held),
                    printing = _jobs.Count(x => x.Status == JobStatus.Printing),
                    done = _jobs.Count(x => x.Status == JobStatus.Done),
                    failed = _jobs.Count(x => x.Status == JobStatus.Failed),
                    workerRunning = IsWorkerRunning
                };
            }
        }

        public PrintJob Enqueue(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (job.Copies < 1) job.Copies = 1;
            job.Id = Guid.NewGuid().ToString("N");
            job.EnqueuedAt = DateTime.Now;

            // 打印管理钩子：策略评估 → 配额检查 → 存档。失败静默不影响主流程之外的可用性。
            try
            {
                var decision = PolicyEvaluator.Apply(job);
                if (!decision.Allowed)
                {
                    Tools.Logger("策略拒绝入队: " + decision.Reason);
                    job.Error = decision.Reason;
                    return job.Clone();
                }
                var quotaErr = ManagementStore.CheckQuota(job.UserId, Math.Max(1, job.Copies), job.Color == true);
                if (!string.IsNullOrEmpty(quotaErr))
                {
                    Tools.Logger("配额拒绝入队: " + quotaErr);
                    job.Error = quotaErr;
                    return job.Clone();
                }
                ArchiveService.ArchiveJob(job);
            }
            catch (Exception ex) { Tools.Logger("入队前置钩子失败(放行): " + ex.Message); }

            // 安全打印：releaseRequired 的任务挂起为 held，等待 PIN/工卡认证释放，绝不自动出纸
            var held = job.ReleaseRequired;

            // 计划任务：到点才真正进队列。未到时先暂存（持久化，重启不丢），
            // 工作线程稍后轮询到点释放；已到期（或 DueAt 为空）立即入队并唤醒工作线程。
            var due = job.DueAt == null || job.DueAt.Value <= DateTime.Now;
            var immediate = due;

            // 幂等去重：调用方提供 idempotencyKey，或按 源+打印机+参数 自动派生。
            // 相同键且已存在「未完成（pending/printing）」任务时，不重复入队，直接返回已有任务。
            if (string.IsNullOrWhiteSpace(job.IdempotencyKey))
                job.IdempotencyKey = DeriveIdempotencyKey(job);
            string dedupKey = job.IdempotencyKey;
            bool deduped = false;
            lock (_gate)
            {
                var dup = _jobs.FirstOrDefault(x =>
                    x.Id == job.Id ||
                    (!string.IsNullOrEmpty(dedupKey) &&
                     x.IdempotencyKey == dedupKey &&
                     (x.Status == JobStatus.Pending || x.Status == JobStatus.Printing ||
                      x.Status == JobStatus.Held)));
                if (dup != null)
                {
                    // 幂等命中：沿用已有任务；若已有任务尚未开始且本次给了更早的触发时间，更新 DueAt
                    if (job.DueAt != null && dup.DueAt == null && !due)
                        dup.DueAt = job.DueAt.Value;
                    job = dup;
                    job.Deduped = true;
                    deduped = true;
                    immediate = dup.DueAt != null && dup.DueAt.Value <= DateTime.Now;
                }
                else
                {
                    // 安全打印任务挂起为 held；其余按计划时间决定 pending / 暂存
                    job.Status = held ? JobStatus.Held : JobStatus.Pending;
                    job.StartedAt = null;
                    job.FinishedAt = null;
                    job.Error = "";
                    _jobs.Add(job);
                }
            }
            if (deduped)
            {
                Tools.Logger("幂等去重: " + dedupKey + " 已存在，跳过重复入队 → " + job.Id);
                PersistAndNotify();
                if (immediate && job.DueAt != null && job.Status != JobStatus.Held)
                {
                    // 命中但已到期：补发信号让它参与消费（原来暂存任务没被唤醒）
                    _signal.Release();
                }
                return job.Clone();
            }
            if (held)
            {
                // 安全打印：不入待打印队列，等待认证释放
                PersistAndNotify();
                Tools.Logger("安全打印任务挂起等待认证: " + job.Id + " → " + job.Target);
            }
            else if (immediate)
            {
                PersistAndNotify();
                _signal.Release();
                Tools.Logger("任务入队: " + job.Id + " → " + job.Target);
            }
            else
            {
                // 计划任务暂存：持久化即可，工作线程轮询到点释放
                PersistAndNotify();
                Tools.Logger("计划任务暂存: " + job.Id + " → " + job.Target +
                             "，到点 " + job.DueAt.Value + " 后自动入队打印");
            }
            return job.Clone();
        }

        /// <summary>幂等键派生：源 + 打印机 + 内容特征 + 打印参数（不含 Id/Timestamp），同一请求重复调用得到同一键。</summary>
        private static string DeriveIdempotencyKey(PrintJob job)
        {
            var raw = string.Join("|",
                job.Source ?? "",
                job.Target ?? "",
                job.Printer ?? "",
                job.Copies,
                job.Fit, job.Scale, job.PaperKind, job.Pages, job.Duplex,
                (job.Color.HasValue ? (job.Color.Value ? "c" : "m") : "a"),
                job.Margin, job.Bin, job.Dpi, job.PageOrder);
            // SHA1 前 16 字节，稳定且短
            using var sha = System.Security.Cryptography.SHA1.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        public bool Retry(string id)
        {
            bool ok;
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                if (job == null) return false;
                // 存档重放：原文件已被删除时从 data/archive/{jobId}/ 恢复
                if (job.Source != "url" && !string.IsNullOrWhiteSpace(job.Target) && !File.Exists(job.Target))
                    ArchiveService.RestoreForReplay(job);
                // 重试任务前置，保证下次消费优先处理
                _jobs.Remove(job);
                job.Status = JobStatus.Pending;
                job.Error = "";
                job.StartedAt = null;
                job.FinishedAt = null;
                _jobs.Insert(0, job);
                ok = true;
            }
            PersistAndNotify();
            _signal.Release();
            return ok;
        }

        public bool Cancel(string id)
        {
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                if (job == null || job.Status == JobStatus.Printing) return false;
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.Now;
            }
            PersistAndNotify();
            return true;
        }

        public bool Remove(string id)
        {
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                if (job == null || job.Status == JobStatus.Printing) return false;
                _jobs.Remove(job);
            }
            PersistAndNotify();
            return true;
        }

        /// <summary>
        /// 召回（可撤回）：把「已入队、尚未开始打印」的任务撤下队列，并记一条审计留痕，
        /// 供物流中心核对「这单为什么没打 / 被谁撤回」。正在打印（printing）或已出纸的任务无法撤回——
        /// 裸 RAW / IPP 网络直发一旦出纸即不可撤销，只能靠审计回执追溯，此方法对其返回 false。
        /// </summary>
        public string Recall(string id, string reason)
        {
            bool found;
            string status;
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                found = job != null;
                if (!found) return "任务不存在: " + id;
                status = job.Status;
                if (job.Status == JobStatus.Printing)
                    return "任务正在打印，无法撤回（出纸后不可撤销，需按审计回执追溯）";
                // pending / done / failed / cancelled：撤回即标记 cancelled 并留痕
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.Now;
                job.Error = "召回: " + (reason ?? "");
            }
            PersistAndNotify();
            try { PrintEventStore.Record("recall:" + id, "Queue", 0, 1, true, "召回撤回，原因: " + (reason ?? "-")); }
            catch { /* 审计失败不影响撤回动作 */ }
            return "任务已召回撤下（原状态 " + status + "）";
        }

        /// <summary>按幂等键查未完成任务（pending/printing）；用于入队前判断是否已存在。</summary>
        public PrintJob FindByDedupKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_gate)
                return _jobs.FirstOrDefault(x => x.IdempotencyKey == key &&
                    (x.Status == JobStatus.Pending || x.Status == JobStatus.Printing))?.Clone();
        }

        /// <summary>安全打印：认证通过后把 held 任务释放为 pending 并唤醒工作线程。</summary>
        public bool ReleaseHeld(string id, string releasedBy)
        {
            bool ok = false;
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                if (job != null && job.Status == JobStatus.Held)
                {
                    job.Status = JobStatus.Pending;
                    job.ReleasedBy = releasedBy ?? "";
                    job.ReleasedAt = DateTime.Now;
                    job.PinHash = ""; // 释放后清除 PIN 哈希
                    ok = true;
                }
            }
            if (ok) { PersistAndNotify(); _signal.Release(); }
            return ok;
        }

        /// <summary>安全打印：列出所有等待认证的任务。</summary>
        public List<PrintJob> HeldJobs()
        {
            lock (_gate)
                return _jobs.Where(x => x.Status == JobStatus.Held)
                            .OrderBy(x => x.EnqueuedAt).Select(x => x.Clone()).ToList();
        }

        /// <summary>clearFinished=true 只清已完成/失败/取消；false 清空全部（printing 保留）。</summary>
        public int Clear(bool clearFinished)
        {
            int removed;
            lock (_gate)
            {
                removed = _jobs.RemoveAll(x => x.Status != JobStatus.Printing &&
                    (!clearFinished || x.Status != JobStatus.Pending));
            }
            PersistAndNotify();
            return removed;
        }

        private async Task WorkerLoop()
        {
            IsWorkerRunning = true;
            Tools.Logger("打印队列工作线程已启动");
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // 带 1s 超时轮询：即使队列里只有「未到点的计划任务」（无新信号唤醒），
                    // 也能到点释放并打印。超时返回 false 属正常，直接进入下面的到点检查。
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                // 一次信号后持续消费，直到没有「可立即打印」的任务；计划任务（DueAt 未到）跳过
                while (!_cts.IsCancellationRequested)
                {
                    PrintJob next;
                    int releasedPlan = 0;
                    lock (_gate)
                    {
                        var now = DateTime.Now;
                        // ① 计划任务到点释放：DueAt <= now 的任务标记为可立即打印
                        foreach (var p in _jobs.Where(x => x.Status == JobStatus.Pending && x.DueAt != null))
                            if (p.DueAt.Value <= now) { p.DueAt = null; releasedPlan++; }

                        // ② 批次优先：同一 groupId 的 pending 任务按 GroupOrder 顺序消费；
                        // 无 groupId 的任务保持入队 FIFO。批次内按序，批次之间按组首次出现的 FIFO。
                        var pending = _jobs.Where(x => x.Status == JobStatus.Pending && x.DueAt == null).ToList();
                        var grouped = pending.Where(x => !string.IsNullOrEmpty(x.GroupId))
                            .GroupBy(x => x.GroupId)
                            .OrderBy(g => g.Min(x => x.EnqueuedAt));
                        var ungrouped = pending.Where(x => string.IsNullOrEmpty(x.GroupId));
                        next = null;
                        bool found = false;
                        foreach (var g in grouped)
                        {
                            var top = g.OrderBy(x => x.GroupOrder).First();
                            if (top.Status == JobStatus.Pending) { next = top; found = true; break; }
                        }
                        if (!found) { next = ungrouped.FirstOrDefault(); found = next != null; }
                        if (next == null) break;
                        next.Status = JobStatus.Printing;
                        next.StartedAt = DateTime.Now;
                        next.Attempts++;
                    }
                    if (releasedPlan > 0) Tools.Logger("计划任务到点释放: " + releasedPlan + " 个任务进入可打印状态");
                    Notify();

                    var outcome = _printService.Print(next);

                    lock (_gate)
                    {
                        next.Status = outcome.Ok ? JobStatus.Done : JobStatus.Failed;
                        next.FinishedAt = DateTime.Now;
                        next.Error = outcome.Ok ? "" : outcome.Message;
                    }
                    // 计费与费用流水（仅成功任务；页数按份数计，彩色/黑白单价来自配置）
                    if (outcome.Ok)
                    {
                        try
                        {
                            var st = SettingsStore.Instance.Current;
                            var pages = Math.Max(1, next.Copies);
                            var isColor = next.Color == true;
                            var unit = isColor ? st.CostPerPageColor : st.CostPerPageBW;
                            var amount = (decimal)unit * pages;
                            next.BillablePages = pages;
                            ManagementStore.Charge(next.UserId, next.Printer, next.Id, pages, isColor, amount);
                        }
                        catch (Exception ex) { Tools.Logger("任务计费失败(不影响打印): " + ex.Message); }
                    }
                    Tools.Logger(string.Format("任务 {0} {1}: {2}",
                        next.Id, next.Status, outcome.Message));
                    Save();
                    Notify();
                }
            }
            IsWorkerRunning = false;
        }

        public void Shutdown()
        {
            try { _cts.Cancel(); } catch { /* 忽略 */ }
        }

        private void PersistAndNotify()
        {
            Save();
            Notify();
        }

        private void Notify()
        {
            var handler = Changed;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { Tools.Logger("队列通知失败: " + ex.Message); }
            }
        }

        private void Save()
        {
            try
            {
                string json;
                lock (_gate) json = JsonConvert.SerializeObject(_jobs, Formatting.Indented);
                SettingsStore.AtomicWrite(SettingsStore.Instance.QueuePath, json);
            }
            catch (Exception ex) { Tools.Logger("队列持久化失败: " + ex.Message); }
        }

        private void Load()
        {
            try
            {
                var path = SettingsStore.Instance.QueuePath;
                if (!File.Exists(path)) return;
                var loaded = JsonConvert.DeserializeObject<List<PrintJob>>(File.ReadAllText(path));
                if (loaded == null) return;
                var recovered = 0;
                foreach (var job in loaded)
                {
                    if (job.Status == JobStatus.Printing)
                    {
                        job.Status = JobStatus.Pending;
                        job.Error = "应用重启，任务自动恢复";
                        recovered++;
                    }
                }
                // 计划任务（DueAt 未到）：暂存不唤醒工作线程，到点后由轮询自动释放
                int planned = loaded.Count(x => x.Status == JobStatus.Pending && x.DueAt != null && x.DueAt.Value > DateTime.Now);
                int immediate = loaded.Count(x => x.Status == JobStatus.Pending && (x.DueAt == null || x.DueAt.Value <= DateTime.Now));
                lock (_gate) _jobs.AddRange(loaded);
                if (recovered > 0) Tools.Logger("崩溃恢复: " + recovered + " 个任务重置为 pending");
                if (planned > 0) Tools.Logger("计划任务恢复: " + planned + " 个暂存计划任务已载入，到点自动打印");
                for (int i = 0; i < immediate; i++) _signal.Release();
            }
            catch (Exception ex) { Tools.Logger("队列加载失败: " + ex.Message); }
        }
    }
}

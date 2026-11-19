// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Models;
using DuoAssistant.Utils;
using Newtonsoft.Json;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 排期 / 计划任务：本地定时器把到点的计划任务放入打印队列。
    /// 计划任务持久化到 data/scheduled.json，崩溃/重启后恢复未触发者；
    /// 触发时交给 PrintQueueService（自动获得幂等去重 / 批次顺序 / 断网补打）。
    /// 纯本地能力，不上行、不依赖服务端调度；触发即记一条审计。
    /// </summary>
    public sealed class ScheduledPrintScheduler
    {
        private static readonly Lazy<ScheduledPrintScheduler> Lazy =
            new Lazy<ScheduledPrintScheduler>(() => new ScheduledPrintScheduler(), true);
        public static ScheduledPrintScheduler Instance => Lazy.Value;

        /// <summary>一条计划任务（持久化结构）。</summary>
        public class Plan
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            /// <summary>触发时间（UTC ISO8601）。一次性任务，到点执行后置 Done 不再重复。</summary>
            public DateTime DueAt { get; set; }
            /// <summary>任务体：打印 job 字段（target/printer/copies/… 同 QueuePrint）。</summary>
            public PrintJob Job { get; set; } = new PrintJob();
            /// <summary>可选幂等键（不填则按 job 内容派生，触发重复也不重打）。</summary>
            public string IdempotencyKey { get; set; } = "";
            /// <summary>可选备注（波次/计划名）。</summary>
            public string Note { get; set; } = "";
            /// <summary>状态：pending（未触发）/ done（已触发）/ cancelled。</summary>
            public string Status { get; set; } = "pending";
            /// <summary>实际触发时间（UTC）。</summary>
            public DateTime? FiredAt { get; set; }
        }

        private readonly object _gate = new object();
        private readonly List<Plan> _plans = new List<Plan>();
        private Timer _timer;
        private readonly string _path;

        private ScheduledPrintScheduler()
        {
            _path = System.IO.Path.Combine(SettingsStore.Instance.DataDir, "scheduled.json");
            Load();
        }

        public bool IsRunning => _timer != null;

        /// <summary>启动后台定时器（幂等）。每 5s 扫描一次到点计划。</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_timer != null) return;
                _timer = new Timer(_ => TickSafe(), null, 5000, 5000);
                Tools.Logger("排期调度器已启动（5s 轮询）");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        public List<Plan> GetAll()
        {
            lock (_gate) return _plans.Select(ClonePlan).ToList();
        }

        public object Summary()
        {
            lock (_gate)
            {
                return new
                {
                    running = _timer != null,
                    total = _plans.Count,
                    pending = _plans.Count(x => x.Status == "pending"),
                    done = _plans.Count(x => x.Status == "done"),
                    nextDue = _plans.Where(x => x.Status == "pending")
                        .OrderBy(x => x.DueAt).Select(x => x.DueAt).FirstOrDefault()
                };
            }
        }

        /// <summary>新增/排一个计划任务，立即返回其 Id。</summary>
        public Plan Add(Plan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.DueAt <= DateTime.UtcNow) plan.DueAt = DateTime.UtcNow.AddSeconds(1);
            if (string.IsNullOrWhiteSpace(plan.Id)) plan.Id = Guid.NewGuid().ToString("N");
            plan.Status = "pending";
            lock (_gate) _plans.Add(plan);
            Save();
            Tools.Logger("计划任务入列: " + plan.Id + " 到点 " + plan.DueAt.ToLocalTime());
            return ClonePlan(plan);
        }

        public bool Remove(string id)
        {
            bool ok;
            lock (_gate)
            {
                var p = _plans.FirstOrDefault(x => x.Id == id);
                if (p == null) return false;
                if (p.Status == "pending") p.Status = "cancelled";
                ok = true;
            }
            Save();
            return ok;
        }

        /// <summary>手动触发某计划（提前执行，不等到点）。</summary>
        public bool Now(string id)
        {
            lock (_gate)
            {
                var p = _plans.FirstOrDefault(x => x.Id == id);
                if (p == null || p.Status != "pending") return false;
                p.DueAt = DateTime.UtcNow;
            }
            Save();
            return true;
        }

        private void TickSafe()
        {
            try { Tick(); }
            catch (Exception ex) { Tools.Logger("排期调度异常: " + ex.Message); }
        }

        private void Tick()
        {
            List<Plan> due;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                due = _plans.Where(x => x.Status == "pending" && x.DueAt <= now).ToList();
                foreach (var p in due) p.Status = "firing"; // 先占位，避免并发重复触发
            }
            if (due.Count == 0) return;
            foreach (var p in due) Fire(p);
            Save();
        }

        private void Fire(Plan p)
        {
            var job = p.Job ?? new PrintJob();
            if (string.IsNullOrWhiteSpace(p.IdempotencyKey)) p.IdempotencyKey = "";
            job.IdempotencyKey = p.IdempotencyKey; // 交给队列去重，重复触发不重打
            job.GroupId = string.IsNullOrEmpty(job.GroupId) ? ("plan:" + p.Id) : job.GroupId;
            var queued = PrintQueueService.Instance.Enqueue(job);
            p.Status = "done";
            p.FiredAt = DateTime.UtcNow;
            Tools.Logger("计划任务触发: " + p.Id + " → 队列 " + queued.Id +
                         (queued.Deduped ? "（幂等命中，未重复入队）" : ""));
            try { PrintEventStore.Record("plan:" + p.Id, "Scheduler", 0, job.Copies, true,
                "排期触发入队，队列任务 " + queued.Id + (queued.Deduped ? "（幂等去重）" : "")); }
            catch { /* 审计失败不影响触发 */ }
        }

        // ------------------------------------------------ 持久化
        private void Save()
        {
            try
            {
                string json;
                lock (_gate) json = JsonConvert.SerializeObject(_plans, Formatting.Indented);
                SettingsStore.AtomicWrite(_path, json);
            }
            catch (Exception ex) { Tools.Logger("排期持久化失败: " + ex.Message); }
        }

        private void Load()
        {
            try
            {
                if (!System.IO.File.Exists(_path)) return;
                var loaded = JsonConvert.DeserializeObject<List<Plan>>(System.IO.File.ReadAllText(_path));
                if (loaded == null) return;
                // 崩溃恢复：firing 复位 pending（重启前没打完的下轮再触发）
                foreach (var p in loaded)
                    if (p.Status == "firing") p.Status = "pending";
                lock (_gate) _plans.AddRange(loaded);
            }
            catch (Exception ex) { Tools.Logger("排期加载失败: " + ex.Message); }
        }

        private static Plan ClonePlan(Plan p) =>
            JsonConvert.DeserializeObject<Plan>(JsonConvert.SerializeObject(p));

    }
}

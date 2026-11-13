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
            job.Status = JobStatus.Pending;
            job.EnqueuedAt = DateTime.Now;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.Error = "";
            lock (_gate) _jobs.Add(job);
            PersistAndNotify();
            _signal.Release();
            Tools.Logger("任务入队: " + job.Id + " → " + job.Target);
            return job.Clone();
        }

        public bool Retry(string id)
        {
            bool ok;
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(x => x.Id == id);
                if (job == null) return false;
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
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                // 一次信号后持续消费，直到没有 pending 任务
                while (!_cts.IsCancellationRequested)
                {
                    PrintJob next;
                    lock (_gate)
                    {
                        next = _jobs.FirstOrDefault(x => x.Status == JobStatus.Pending);
                        if (next == null) break;
                        next.Status = JobStatus.Printing;
                        next.StartedAt = DateTime.Now;
                        next.Attempts++;
                    }
                    Notify();

                    var outcome = _printService.Print(next);

                    lock (_gate)
                    {
                        next.Status = outcome.Ok ? JobStatus.Done : JobStatus.Failed;
                        next.FinishedAt = DateTime.Now;
                        next.Error = outcome.Ok ? "" : outcome.Message;
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
                lock (_gate) _jobs.AddRange(loaded);
                if (recovered > 0) Tools.Logger("崩溃恢复: " + recovered + " 个任务重置为 pending");
                // 有待处理任务时唤醒工作线程
                for (int i = 0; i < _jobs.Count(x => x.Status == JobStatus.Pending); i++) _signal.Release();
            }
            catch (Exception ex) { Tools.Logger("队列加载失败: " + ex.Message); }
        }
    }
}

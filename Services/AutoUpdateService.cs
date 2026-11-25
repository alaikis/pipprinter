// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 自动检查更新：启动后延迟检查一次，之后按配置间隔在后台定时检查。
    /// 只做「检查 + 通知」，不自动下载、不打断用户；发现新版本时通过回调交给 UI（托盘气泡）。
    /// 断网、更新服务不可达时不发请求：本地网卡判定 + 网络变化事件 + 失败退避三段组合，
    /// 全程不额外发探测包；网络恢复后去抖补查一次。
    /// 配置自动读取 SettingsStore.Current，设置页保存后调用 Reconfigure() 即时生效。
    /// </summary>
    public sealed class AutoUpdateService
    {
        // 启动后首次检查延迟：避开启动高峰与网络未就绪窗口。
        private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
        // 间隔兜底：异常配置也不会让定时器过于频繁。
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);
        // 网络变化后延迟补查：网卡/DHCP 事件常成串出现，去抖避免连发请求。
        private static readonly TimeSpan NetworkResumeDelay = TimeSpan.FromSeconds(20);
        // 更新服务不可达时的退避：期间只做本地判断，不发请求；网络变化可提前解除。
        private static readonly TimeSpan UnreachableBackoff = TimeSpan.FromMinutes(30);
        // 离线抑制的有限重查窗口：防止某些环境不触发网络变化事件时长期漏查（只查本地网卡，零流量）。
        private static readonly TimeSpan OfflineRecheck = TimeSpan.FromMinutes(10);
        // 后台单次检查超时：避免 HttpClient 默认 10 分钟超时长期占住检查槽。
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

        private static readonly Lazy<AutoUpdateService> Lazy =
            new Lazy<AutoUpdateService>(() => new AutoUpdateService(), true);

        public static AutoUpdateService Instance => Lazy.Value;

        private readonly object _gate = new object();
        private Timer _timer;
        private Timer _resumeTimer;
        private bool _eventsHooked;
        private Action<UpdateInfo> _onFound;
        private string _lastNotifiedVersion;
        private int _checking; // 0=空闲 1=检查中，防止定时与手动并发重入
        private bool _suppressed; // 离线或更新服务不可达，暂停自动检查
        private DateTime _suppressedUntilUtc; // 到此时间后可再次尝试；网络变化事件可提前解除

        private AutoUpdateService() { }

        /// <summary>
        /// 启动自动检查。onFound 在线程池上下文触发（检查在后台），调用方需自行切回 UI 线程。
        /// 需在 Application.Run 之后调用；重复调用安全（先停旧定时器）。
        /// </summary>
        public void Start(Action<UpdateInfo> onFound)
        {
            lock (_gate)
            {
                _onFound = onFound;
                if (!_eventsHooked)
                {
                    NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
                    NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                    _eventsHooked = true;
                }
                RestartTimerLocked();
            }
        }

        /// <summary>设置页保存后调用：按最新配置重建定时器（开关/间隔即时生效）。</summary>
        public void Reconfigure()
        {
            lock (_gate) { RestartTimerLocked(); }
        }

        /// <summary>应用退出时停止定时器并注销网络事件。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _resumeTimer?.Dispose();
                _resumeTimer = null;
                if (_eventsHooked)
                {
                    NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                    NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                    _eventsHooked = false;
                }
            }
        }

        private void RestartTimerLocked()
        {
            _timer?.Dispose();
            _timer = null;
            _resumeTimer?.Dispose();
            _resumeTimer = null;
            var s = SettingsStore.Instance.Current;
            if (!s.AutoCheckUpdate) return;

            var interval = TimeSpan.FromHours(s.AutoCheckIntervalHours > 0 ? s.AutoCheckIntervalHours : 6);
            if (interval < MinInterval) interval = MinInterval;
            // 首次延迟后触发，之后每 interval 重复。Timer 回调在线程池，不占用 UI。
            _timer = new Timer(_ => _ = CheckAsync(), null, FirstDelay, interval);
        }

        /// <summary>网络状态变化：离线立即抑制，恢复后去抖补查一次（不等下个 6 小时）。</summary>
        private void OnNetworkChanged(object sender, EventArgs e)
        {
            try
            {
                if (!IsOnline())
                {
                    bool changed;
                    lock (_gate)
                    {
                        changed = !_suppressed;
                        _suppressed = true;
                        _suppressedUntilUtc = DateTime.UtcNow.Add(OfflineRecheck);
                        ScheduleResumeLocked(OfflineRecheck);
                    }
                    if (changed) Tools.Logger("自动检查更新：网络已断开，暂停检查");
                    return;
                }

                bool wasSuppressed;
                lock (_gate)
                {
                    wasSuppressed = _suppressed;
                    _suppressed = false;
                    _suppressedUntilUtc = default;
                    ScheduleResumeLocked();
                }
                if (wasSuppressed) Tools.Logger("自动检查更新：网络已恢复，将在 20 秒后补查");
            }
            catch (Exception ex)
            {
                Tools.Logger("处理网络变化失败: " + ex.Message);
            }
        }

        private void ScheduleResumeLocked(TimeSpan? delay = null)
        {
            if (!SettingsStore.Instance.Current.AutoCheckUpdate) return;
            _resumeTimer?.Dispose();
            _resumeTimer = new Timer(_ => _ = CheckAsync(ignoreSuppression: true),
                null, delay ?? NetworkResumeDelay, Timeout.InfiniteTimeSpan);
        }

        /// <summary>执行一次后台检查；供定时器调用，也可由外部手动触发（带重入保护）。</summary>
        public Task CheckAsync() => CheckAsync(false);

        private async Task CheckAsync(bool ignoreSuppression)
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;
            try
            {
                var s = SettingsStore.Instance.Current;
                if (!s.AutoCheckUpdate) return;

                // 任何检查前都先看本地网卡状态：不发网络包，断网/飞行模式零请求。
                if (!IsOnline())
                {
                    // 离线期间每 10 分钟只做一次本地网卡复查，不发任何网络请求；
                    // 真正的网络变化事件会把补查提前到 20 秒后。
                    lock (_gate)
                    {
                        _suppressed = true;
                        _suppressedUntilUtc = DateTime.UtcNow.Add(OfflineRecheck);
                        ScheduleResumeLocked(OfflineRecheck);
                    }
                    Tools.Logger("自动检查更新：当前离线，已跳过");
                    return;
                }
                // 上次请求已判定更新服务不可达：退避期内不再重试，网络变化可提前解除。
                if (!ignoreSuppression && IsSuppressed())
                {
                    Tools.Logger("自动检查更新：更新服务暂不可达，退避中已跳过");
                    return;
                }

                UpdateInfo info;
                try
                {
                    using (var cts = new CancellationTokenSource(CheckTimeout))
                    {
                        info = await new UpdateService().CheckUpdateAsync(cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    MarkUnreachable();
                    return;
                }

                if (info == null) return;
                if (info.NetworkError)
                {
                    MarkUnreachable();
                    return;
                }
                lock (_gate) { _suppressed = false; _suppressedUntilUtc = default; }

                if (!info.HasUpdate || string.IsNullOrWhiteSpace(info.Remote)) return;

                // 同一版本只通知一次（进程内）；用户点开更新流程若失败，下次不再重复弹，
                // 仍可通过托盘菜单/设置页手动检查，避免后台反复打扰。
                if (string.Equals(info.Remote, _lastNotifiedVersion, StringComparison.OrdinalIgnoreCase))
                    return;
                _lastNotifiedVersion = info.Remote;


                // 闲时静默升级分支：若 AutoSilentUpdate 启用且当前处于「闲时」（用户空闲 + 打印队列空闲），
                // 则直接调用 TriggerSilentUpdate（不弹气泡）；否则回退到原有的「气泡提示 + 用户点击」流程。
                var ss = SettingsStore.Instance.Current;
                bool silent = ss.AutoSilentUpdate &&
                              DuoAssistant.Utils.IdleDetector.IsIdle(ss.SilentUpdateIdleMinutes) &&
                              IsQueueIdle();
                if (silent)
                {
                    Tools.Logger("自动检查更新：发现 v" + info.Remote + "，静默升级模式 → 尝试 TriggerSilentUpdate");
                    try
                    {
                        var res = Newtonsoft.Json.Linq.JObject.Parse(
                            CommandDispatcher.Execute("TriggerSilentUpdate", "{}"));
                        var code = res["code"]?.Value<int>(null);
                        var reason = res["data"]?["reason"]?.ToString();
                        if (code == 200)
                            Tools.Logger("静默升级结果: " + (reason ?? "已替换"));
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger("静默升级失败: " + ex.Message);
                    }
                }
                else
                {
                    Action<UpdateInfo> cb;
                    lock (_gate) { cb = _onFound; }
                    cb?.Invoke(info);
                }
            }
            catch (Exception ex)
            {
                // 自动检查属于后台增强，任何异常都不影响主程序
                Tools.Logger("自动检查更新失败: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        private void MarkUnreachable()
        {
            lock (_gate)
            {
                _suppressed = true;
                _suppressedUntilUtc = DateTime.UtcNow.Add(UnreachableBackoff);
            }
            Tools.Logger("自动检查更新：更新服务不可达，暂停 30 分钟；网络恢复时立即补查");
        }

        private bool IsSuppressed()
        {
            lock (_gate)
            {
                if (!_suppressed) return false;
                if (DateTime.UtcNow >= _suppressedUntilUtc)
                {
                    _suppressed = false;
                    _suppressedUntilUtc = default;
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 是否在线：只查本地网卡状态，不发任何网络包，零流量开销。
        /// GetIsNetworkAvailable 在装有虚拟网卡（Docker/VMware）且物理断网时可能误报在线，
        /// 因此要求至少一块「已启用、有可用单播地址、有网关」的网卡，避免离线时仍发更新请求。
        /// APIPA（169.254.x.x）与无网关的虚拟网卡不算联网。
        /// </summary>
        private static bool IsOnline()
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    var props = ni.GetIPProperties();
                    bool hasAddress = false;
                    foreach (var ua in props.UnicastAddresses)
                        if (IsUsableAddress(ua.Address)) { hasAddress = true; break; }
                    if (!hasAddress) continue;

                    foreach (var gw in props.GatewayAddresses)
                        if (IsUsableAddress(gw.Address)) return true;
                }
                return false;
            }
            catch
            {
                // 枚举失败时保守按 GetIsNetworkAvailable 的结果处理
                return NetworkInterface.GetIsNetworkAvailable();
            }
        }

        private static bool IsUsableAddress(IPAddress a)
        {
            if (a == null || IPAddress.IsLoopback(a) ||
                a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;

            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                // APIPA：DHCP 未拿到地址时系统自分的地址，不代表真的能联网
                return !(b[0] == 169 && b[1] == 254);
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = a.GetAddressBytes();
                // fe80::/10 链路本地地址不算联网证据
                return !(b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
            }
            return false;
        }

        /// <summary>打印队列是否空闲：无 pending / printing / held 任务。</summary>
        private static bool IsQueueIdle()
        {
            try
            {
                var summary = (dynamic)DuoAssistant.Services.PrintQueueService.Instance.Summary();
                return (long)summary.pending == 0 &&
                       (long)summary.printing == 0 &&
                       (long)summary.held == 0;
            }
            catch
            {
                return true;
            }
        }
    }
}

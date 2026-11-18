// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 自动检查更新：启动后延迟检查一次，之后按配置间隔在后台定时检查。
    /// 只做「检查 + 通知」，不自动下载、不打断用户；发现新版本时通过回调交给 UI（托盘气泡）。
    /// 网络失败静默记日志；同一版本在一次进程内只通知一次，避免反复打扰。
    /// 配置自动读取 SettingsStore.Current，设置页保存后调用 Reconfigure() 即时生效。
    /// </summary>
    public sealed class AutoUpdateService
    {
        // 启动后首次检查延迟：避开启动高峰与网络未就绪窗口。
        private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
        // 间隔兜底：异常配置也不会让定时器过于频繁。
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

        private static readonly Lazy<AutoUpdateService> Lazy =
            new Lazy<AutoUpdateService>(() => new AutoUpdateService(), true);

        public static AutoUpdateService Instance => Lazy.Value;

        private readonly object _gate = new object();
        private Timer _timer;
        private Action<UpdateInfo> _onFound;
        private string _lastNotifiedVersion;
        private int _checking; // 0=空闲 1=检查中，防止定时与手动并发重入

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
                RestartTimerLocked();
            }
        }

        /// <summary>设置页保存后调用：按最新配置重建定时器（开关/间隔即时生效）。</summary>
        public void Reconfigure()
        {
            lock (_gate) { RestartTimerLocked(); }
        }

        /// <summary>应用退出时停止定时器。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void RestartTimerLocked()
        {
            _timer?.Dispose();
            _timer = null;
            var s = SettingsStore.Instance.Current;
            if (!s.AutoCheckUpdate) return;

            var interval = TimeSpan.FromHours(s.AutoCheckIntervalHours > 0 ? s.AutoCheckIntervalHours : 6);
            if (interval < MinInterval) interval = MinInterval;
            // 首次延迟后触发，之后每 interval 重复。Timer 回调在线程池，不占用 UI。
            _timer = new Timer(_ => _ = CheckAsync(), null, FirstDelay, interval);
        }

        /// <summary>执行一次后台检查；供定时器调用，也可由外部手动触发（带重入保护）。</summary>
        public async Task CheckAsync()
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;
            try
            {
                var s = SettingsStore.Instance.Current;
                if (!s.AutoCheckUpdate) return;

                // 离线时直接跳过，不发起任何网络请求（GetIsNetworkAvailable 只查本地网卡状态，
                // 本身不产生流量）。避免断网/飞行模式下定时器反复唤醒带来的无效请求与资源占用。
                if (!IsOnline())
                {
                    Tools.Logger("自动检查更新：当前离线，已跳过");
                    return;
                }

                var info = await new UpdateService().CheckUpdateAsync().ConfigureAwait(false);
                if (info == null || !info.HasUpdate || string.IsNullOrWhiteSpace(info.Remote)) return;

                // 同一版本只通知一次（进程内）；用户点开更新流程若失败，下次不再重复弹，
                // 仍可通过托盘菜单/设置页手动检查，避免后台反复打扰。
                if (string.Equals(info.Remote, _lastNotifiedVersion, StringComparison.OrdinalIgnoreCase))
                    return;
                _lastNotifiedVersion = info.Remote;

                Action<UpdateInfo> cb;
                lock (_gate) { cb = _onFound; }
                cb?.Invoke(info);
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

        /// <summary>
        /// 是否在线：只查本地网卡状态，不发任何网络包，零流量开销。
        /// GetIsNetworkAvailable 在装有虚拟网卡（Docker/VMware）且物理断网时可能误报在线，
        /// 因此额外要求至少有一个「非环回、已启用、已连接」的网卡，避免离线时仍发更新请求。
        /// </summary>
        private static bool IsOnline()
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Unknown)
                        return true;
                }
                return false;
            }
            catch
            {
                // 枚举失败时保守按 GetIsNetworkAvailable 的结果处理
                return true;
            }
        }
    }
}

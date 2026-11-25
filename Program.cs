// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DuoAssistant.Services;
using log4net;
using log4net.Config;
using log4net.Repository;

namespace DuoAssistant
{
    static class Program
    {
        private static Mutex _singleInstance;
        private static WebsockServerService _ws;
        private static WebServerService _web;
        private static Home _home;
        private static readonly AoClientService _ao = AoClientService.Instance;
        private static System.Threading.Timer _tonerTimer;

        [STAThread]
        static void Main(string[] args)
        {
            // 多语言：在任何 UI/消息框之前解析语言（auto 时检测系统 UI 语言，未匹配回退英文）
            Utils.L10n.Init();

            // --headless：跨平台服务模式，只启动 REST/WS + 队列 + 调度器，不创建 WinForms。
            // 供 Linux/macOS 以纯打印服务部署（打印执行走平台适配：Windows GDI / Unix lpr）。
            var headless = Array.Exists(args, a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            if (headless) { RunHeadless(); return; }

            // 单实例（版本感知）：锁名带版本号，避免「旧版占锁 → 新版弹『已在运行』被挡、
            // 永远接管不了 19861」的问题。同版本重复启动仍拒绝（端口只能一个监听者）；
            // 不同版本可共存以便升级替换——新版起来后会探测旧版并提示结束。
            _singleInstance = new Mutex(true,
                @"Global\PipPrinter_" + Utils.Brand.Version.Replace(".", "_"), out bool created);
            if (!created)
            {
                // 同版本已运行：保持旧的「已在运行」语义
                MessageBox.Show(Utils.Brand.Title + Utils.L10n.T("已在系统托盘中运行。"), Utils.L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 版本感知 + 主动接管：若 19861 已被「更低版本」实例占用（升级未重启遗留），
            // 旧版 Kestrel 占着端口，本实例首次绑定必然失败、请求会落到旧版（表现为
            // 「获取不到打印机列表」——旧版无 GDI+/WMI 回退）。这里不止提示，而是
            // 结束旧版进程并等待端口释放，让下方 WebServerService.StartAsync 真正接管 19861。
            bool staleTakenOver = TakeOverStaleInstance();

            // 清理上次自动更新留下的 PipPrinter.exe.old-*（旧进程已退出，此时可删）
            UpdateService.CleanupLeftovers();

            // 启动时自动清理应用自产临时文件（超过 TempAutoCleanMaxAgeDays 天；0=关闭）
            var autoCleanDays = SettingsStore.Instance.Current.TempAutoCleanMaxAgeDays;
            if (autoCleanDays > 0)
            {
                try { new DiskCleanerService().AutoCleanAppTemp(autoCleanDays); }
                catch (Exception ex) { Utils.Tools.Logger("启动自动清理失败: " + ex.Message); }
            }

            // 使用入口程序集对应的默认仓储。原实现 CreateRepository("") 会创建一个命名仓储，
            // 而 Tools.Logger 里的 LogManager.GetLogger("") 解析到的是程序集默认仓储，
            // 两者不一致导致所有分级日志文件始终为空。
            var repository = LogManager.GetRepository(
                System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Program).Assembly);
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "config", "log4net.config");
            if (File.Exists(configPath)) XmlConfigurator.Configure(repository, new FileInfo(configPath));

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = SettingsStore.Instance;
            if (settings.Current.AutoStart)
            {
                try { new BaseServices().StartWhenSystemStart(); }
                catch (Exception ex) { Utils.Tools.Logger("开机自启设置失败: " + ex.Message); }
            }

            // 扫描收件目录监听（多品牌 MFD 扫描到共享文件夹 → 自动入队）
            try { DuoAssistant.Services.InboxWatcherService.Start(); }
            catch (Exception ex) { Utils.Tools.Logger("扫描收件监听启动失败: " + ex.Message); }

            // 墨量 SNMP 定时轮询（间隔来自配置 TonerPollMinutes；0=关闭）
            StartTonerPolling();

            // 通知告警服务（Webhook / 钉钉 / 企微 / SMTP，配置驱动，未启用时零开销）
            try { DuoAssistant.Services.NotificationService.Start(); }
            catch (Exception ex) { Utils.Tools.Logger("通知服务启动失败: " + ex.Message); }

            // 集中式多机后台（配置 CentralApiUrl 才联网上报，否则纯本机）
            try { DuoAssistant.Services.CentralSyncService.Start(); }
            catch (Exception ex) { Utils.Tools.Logger("集中同步启动失败: " + ex.Message); }

            // 打印机定时扫描画像库（配置 AutoScanPrinters=true 才起；间隔 ScanIntervalMinutes）
            // 作为反向路由引擎的数据底座：每轮产出 在线/能力/负载 画像，落库供路由读取。
            try { DuoAssistant.Services.PrinterDiscoveryService.Start(); }
            catch (Exception ex) { Utils.Tools.Logger("打印机定时扫描启动失败: " + ex.Message); }

            _web = new WebServerService();
            // Main 为 STAThread 且尚未进入消息循环，同步等待启动是安全的
            _web.StartAsync().GetAwaiter().GetResult();
            // 若刚结束旧版接管，首次绑定可能因端口尚未释放而失败 → 等端口释放后重试一次。
            if (staleTakenOver && !_web.IsRunning)
            {
                Utils.Tools.Logger("Web 服务首次启动失败（旧版端口释放中），等待后重试: " + (_web.LastError ?? ""));
                var waited = 0;
                while (waited < 15 && PortInUse(SettingsStore.Instance.Current.WebPort))
                { Thread.Sleep(500); waited += 500; }
                _web.StartAsync().GetAwaiter().GetResult();
                if (_web.IsRunning)
                    Utils.Tools.Logger("Web 服务重试启动成功（已接管旧版端口）");
            }

            // WebSocket 与 Web 共用同一 Kestrel 端口（端点 /ws），必须在 Web 启动后挂载
            try
            {
                _ws = new WebsockServerService(_web);
                _ws.Start();
            }
            catch (Exception ex) { Utils.Tools.Logger("WebSocket 启动失败: " + ex); }

            // 打印机画像状态变化（discovered/lost/offline）通过 WS 推送，供前端实时感知。
            try
            {
                DuoAssistant.Services.PrinterDiscoveryService.SetPushHandler((evt, obj) =>
                {
                    try
                    {
                        _web.Broadcast(Newtonsoft.Json.Linq.JObject.FromObject(new
                        {
                            evt,
                            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            data = obj
                        }).ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch (Exception e) { Utils.Tools.Logger("打印机状态事件推送失败: " + e.Message); }
                });
            }
            catch (Exception ex) { Utils.Tools.Logger("打印机状态事件注入失败: " + ex.Message); }

            // Active-Online Print：主动向中继服务器建立出站长连接（仅 AoEnabled=true 时生效）
            try
            {
                _ao.Start();
            }
            catch (Exception ex) { Utils.Tools.Logger("AO 客户端启动失败: " + ex); }

            // 排期调度器：把到点的计划任务放入打印队列（复用队列的幂等去重/批次/断网补打）
            try
            {
                DuoAssistant.Services.ScheduledPrintScheduler.Instance.Start();
            }
            catch (Exception ex) { Utils.Tools.Logger("排期调度器启动失败: " + ex.Message); }

            // 使用无参 Run()：Application.Run(Form) 会自动 Show 主窗体，
            // 覆盖托盘驻留逻辑。静态引用保持窗体与 NotifyIcon 存活。
            // --show 便于首次运行/验收时直接打开主窗口。
            var showUi = Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
            _home = new Home();
            if (showUi) _home.ShowMain(-1);

            // 自动检查更新：启动延迟一次 + 后台按间隔定时；只提示不自动下载。
            // 回调在后台线程触发，由 Home 内部切回 UI 线程弹托盘气泡。
            try
            {
                AutoUpdateService.Instance.Start(info => _home?.OnAutoUpdateFound(info));
            }
            catch (Exception ex) { Utils.Tools.Logger("启动自动检查更新失败: " + ex.Message); }
            // 延迟重启：若静默升级已替换新 exe（PendingRestart=true），后台定时器检测「用户空闲 + 打印队列空闲」后自动重启。
            StartDeferredRestartIfNeeded();


            try
            {
                Application.Run();
            }
            finally
            {
                AutoUpdateService.Instance.Stop();
                StopTonerPolling();
                DuoAssistant.Services.NotificationService.Stop();
                DuoAssistant.Services.CentralSyncService.Stop();
                DuoAssistant.Services.ScheduledPrintScheduler.Instance.Stop();
                DuoAssistant.Services.PrinterDiscoveryService.Stop();
                _web?.StopAsync().GetAwaiter().GetResult();
                _ws?.Stop();
                _ao.Stop();
                DuoAssistant.Services.InboxWatcherService.Stop();
                PrintQueueService.Instance.Shutdown();
                _singleInstance?.ReleaseMutex();
            }
        }

        /// <summary>
        /// 启动「延迟重启」定时器：若 config.PendingRestart=true，每 60s 检测一次
        /// 用户空闲（GetLastInputInfo）+ 打印队列空闲（pending/printing/held 全 0），
        /// 条件满足则 Process.Start(新 exe) + Application.Exit，并清 PendingRestart。
        /// </summary>
        private static System.Threading.Timer _deferredRestartTimer;
        private static void StartDeferredRestartIfNeeded()
        {
            var s = DuoAssistant.Services.SettingsStore.Instance.Current;
            if (!s.PendingRestart) return;
            DuoAssistant.Utils.Tools.Logger("启动：PendingRestart=true，启动延迟重启定时器（每 60s 检测空闲）");
            _deferredRestartTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var cfg = DuoAssistant.Services.SettingsStore.Instance.Current;
                    if (!cfg.PendingRestart) return;

                    var idleMs = DuoAssistant.Utils.IdleDetector.LastInputElapsedMs();
                    if (idleMs < 0) return;
                    var idleNeed = TimeSpan.FromMinutes(Math.Max(0, cfg.SilentUpdateIdleMinutes));
                    if (idleMs < idleNeed.TotalMilliseconds) return;

                    var summary = (dynamic)DuoAssistant.Services.PrintQueueService.Instance.Summary();
                    if ((long)summary.pending > 0 || (long)summary.printing > 0 || (long)summary.held > 0) return;

                    DuoAssistant.Utils.Tools.Logger("延迟重启：条件满足（空闲 + 队列空），启动新 exe");
                    var exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PipPrinter.exe");
                    if (!System.IO.File.Exists(exePath)) return;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
                    DuoAssistant.Services.UpdateService.CleanupLeftovers();
                    DuoAssistant.Services.SettingsStore.Instance.Update(x => x.PendingRestart = false);
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    DuoAssistant.Utils.Tools.Logger("延迟重启失败: " + ex.Message);
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// 探测本机 19861 上是否驻留着「更低版本」的旧实例（升级未重启的遗留进程）。
        /// 旧版 Kestrel 占着端口，本实例首次绑定必然失败、请求会落到旧版上
        /// （表现为「获取不到打印机列表」——旧版无 GDI+/WMI 回退）。
        ///
        /// 这里不止提示，而是<strong>主动接管</strong>：
        ///   1) 打旧版 /health 取版本号，确认是「更低版本」旧实例；
        ///   2) 结束旧版 PipPrinter 进程（排除自身，避免杀掉本实例）；
        ///   3) 返回 true，让调用方在 Web 服务首次绑定失败后等待端口释放并重试，
        ///      使新版真正接管 19861。
        /// 结束旧版失败（权限）时退回旧的「提示」语义，仍不影响启动。
        /// </summary>
        private static bool TakeOverStaleInstance()
        {
            int port = 0;
            try
            {
                port = SettingsStore.Instance.Current.WebPort;
                if (port <= 0) return false;
                var healthUrl = "http://127.0.0.1:" + port + "/health";
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var json = http.GetStringAsync(healthUrl).GetAwaiter().GetResult();
                var version = Newtonsoft.Json.Linq.JToken.Parse(json)["version"]?.ToString();
                if (string.IsNullOrWhiteSpace(version)) return false;
                // 对方比当前版本旧（IsNewer(当前, 对方) 为真 → 当前更新 → 对方是旧版）
                if (!DuoAssistant.Services.UpdateService.IsNewer(Utils.Brand.Version, version)) return false;
                Utils.Tools.Logger("检测到旧版本实例 v" + version + " 占用端口 " + port +
                                   "（当前 v" + Utils.Brand.Version + "），尝试结束旧进程接管端口");

                if (KillStalePipPrinters())
                {
                    // 端口由旧版占用，结束后调用方会等待释放并重试绑定
                    return true;
                }

                // 结束失败（权限/进程已退）：退回提示语义，引导手动处理
                var msg = string.Format(
                    "检测到旧版本 v{0} 仍占用服务端口 {1}，自动结束失败（可能权限不足）。\n" +
                    "当前为 v{2}，旧版本占用会导致新逻辑（如打印机列表）不生效。\n\n" +
                    "请在任务管理器结束名为 PipPrinter 的旧版进程后重启本新版。",
                    version, port, Utils.Brand.Version);
                MessageBox.Show(Utils.Brand.Title + "\r\n" + msg,
                    Utils.L10n.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            catch
            {
                // 端口无响应（旧进程未起 Web 或已退出）或解析失败：静默，不影响启动
                return false;
            }
        }

        /// <summary>
        /// 结束本机其它「PipPrinter」进程（按进程名匹配主 exe，排除自身进程 PID），
        /// 供旧版端口接管使用。只结束名称精确匹配 PipPrinter.exe 的进程，绝不动其它程序。
        /// 返回是否至少结束了一个旧进程。
        /// </summary>
        private static bool KillStalePipPrinters()
        {
            try
            {
                var cur = System.Diagnostics.Process.GetCurrentProcess();
                var curPid = cur.Id;
                var curExe = System.IO.Path.GetFileName(cur.ProcessName) ?? "";
                bool killed = false;
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == curPid) continue;
                        // 只结束与当前主程序同名（PipPrinter.exe）的旧实例；
                        // 不同版本的 exe 名一致（都叫 PipPrinter.exe），靠 PID 排除自身即可区分新旧。
                        var pExe = System.IO.Path.GetFileName(p.ProcessName ?? "");
                        if (string.Equals(pExe, curExe, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(true);
                            killed = true;
                            Utils.Tools.Logger("已结束旧版进程 PID=" + p.Id);
                        }
                    }
                    catch { /* 个别进程取不到信息忽略 */ }
                    finally { p.Dispose(); }
                }
                // 给旧进程一点时间释放端口
                if (killed) Thread.Sleep(800);
                return killed;
            }
            catch (Exception ex)
            {
                Utils.Tools.Logger("结束旧版进程失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>探测某 TCP 端口当前是否仍被占用（127.0.0.1 上能否连上即视为占用）。</summary>
        private static bool PortInUse(int port)
        {
            if (port <= 0) return false;
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var ok = client.BeginConnect("127.0.0.1", port, null, null);
                return ok.AsyncState != null && ok.AsyncWaitHandle.WaitOne(300);
            }
            catch { return false; }
        }

        /// <summary>
        /// 跨平台 headless 服务模式：不引用 WinForms UI（仅用托盘无关的后台服务），
        /// 启动 REST/WS + 队列 + 调度 + 扫描收件监听，主线程阻塞等待退出信号。
        /// </summary>
        static void RunHeadless()
        {
            try
            {
                DuoAssistant.Services.InboxWatcherService.Start();
                StartTonerPolling();
                DuoAssistant.Services.NotificationService.Start();
                DuoAssistant.Services.CentralSyncService.Start();
                _web = new WebServerService();
                _web.StartAsync().GetAwaiter().GetResult();
                _ws = new WebsockServerService(_web);
                _ws.Start();
                _ao.Start();
                DuoAssistant.Services.ScheduledPrintScheduler.Instance.Start();
                Utils.Tools.Logger("Headless 服务模式已启动: " + _web.Endpoint);
                Console.WriteLine("Litchio headless service running at " + _web.Endpoint);
                // 阻塞等待 Ctrl+C / SIGTERM
                using var exit = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; exit.Set(); };
                exit.WaitOne();
            }
            finally
            {
                StopTonerPolling();
                DuoAssistant.Services.ScheduledPrintScheduler.Instance.Stop();
                DuoAssistant.Services.PrinterDiscoveryService.Stop();
                _web?.StopAsync().GetAwaiter().GetResult();
                _ws?.Stop();
                _ao.Stop();
                DuoAssistant.Services.InboxWatcherService.Stop();
                PrintQueueService.Instance.Shutdown();
            }
        }

        private static void StartTonerPolling()
        {
            try
            {
                var minutes = SettingsStore.Instance.Current.TonerPollMinutes;
                if (minutes <= 0) return;
                _tonerTimer = new System.Threading.Timer(_ =>
                {
                    try { DuoAssistant.Services.TonerMonitorService.PollAll(); }
                    catch (Exception ex) { Utils.Tools.Logger("墨量轮询失败: " + ex.Message); }
                }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(minutes));
                Utils.Tools.Logger("墨量 SNMP 轮询已启动，间隔 " + minutes + " 分钟");
            }
            catch (Exception ex) { Utils.Tools.Logger("墨量轮询启动失败: " + ex.Message); }
        }

        private static void StopTonerPolling()
        {
            try { _tonerTimer?.Dispose(); } catch { /* ignore */ }
            _tonerTimer = null;
        }

        /// <summary>供托盘菜单「打开文档站点」使用。</summary>
        public static string DocsUrl => _web?.Endpoint ?? "http://127.0.0.1:19861";

        public static bool WebRunning => _web?.IsRunning ?? false;

        public static bool WsRunning => _ws != null && (_web?.IsRunning ?? false);

        /// <summary>当前 WebSocket 连接数。</summary>
        public static int WebWsConnections => _web?.WsConnectionCount ?? 0;
    }
}

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

            // 单实例：避免 WS/Web 端口被第二个进程抢占
            _singleInstance = new Mutex(true, @"Global\PipPrinter_2_0", out bool created);
            if (!created)
            {
                MessageBox.Show(Utils.Brand.Title + Utils.L10n.T("已在系统托盘中运行。"), Utils.L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

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

            _web = new WebServerService();
            // Main 为 STAThread 且尚未进入消息循环，同步等待启动是安全的
            _web.StartAsync().GetAwaiter().GetResult();

            // WebSocket 与 Web 共用同一 Kestrel 端口（端点 /ws），必须在 Web 启动后挂载
            try
            {
                _ws = new WebsockServerService(_web);
                _ws.Start();
            }
            catch (Exception ex) { Utils.Tools.Logger("WebSocket 启动失败: " + ex); }

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

            try
            {
                Application.Run();
            }
            finally
            {
                AutoUpdateService.Instance.Stop();
                StopTonerPolling();
                DuoAssistant.Services.ScheduledPrintScheduler.Instance.Stop();
                _web?.StopAsync().GetAwaiter().GetResult();
                _ws?.Stop();
                _ao.Stop();
                DuoAssistant.Services.InboxWatcherService.Stop();
                PrintQueueService.Instance.Shutdown();
                _singleInstance?.ReleaseMutex();
            }
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

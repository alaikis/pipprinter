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

        [STAThread]
        static void Main()
        {
            // 多语言：在任何 UI/消息框之前解析语言（auto 时检测系统 UI 语言，未匹配回退英文）
            Utils.L10n.Init();

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
                _web?.StopAsync().GetAwaiter().GetResult();
                _ws?.Stop();
                _ao.Stop();
                PrintQueueService.Instance.Shutdown();
                _singleInstance?.ReleaseMutex();
            }
        }

        /// <summary>供托盘菜单「打开文档站点」使用。</summary>
        public static string DocsUrl => _web?.Endpoint ?? "http://127.0.0.1:19861";

        public static bool WebRunning => _web?.IsRunning ?? false;

        public static bool WsRunning => _ws != null && (_web?.IsRunning ?? false);

        /// <summary>当前 WebSocket 连接数。</summary>
        public static int WebWsConnections => _web?.WsConnectionCount ?? 0;
    }
}

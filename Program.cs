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

        [STAThread]
        static void Main()
        {
            // 单实例：避免 WS/Web 端口被第二个进程抢占
            _singleInstance = new Mutex(true, @"Global\PipPrinter_2_0", out bool created);
            if (!created)
            {
                MessageBox.Show(Utils.Brand.Title + " 已在系统托盘中运行。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
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

            try
            {
                _ws = new WebsockServerService();
                _ws.Start();
            }
            catch (Exception ex) { Utils.Tools.Logger("WebSocket 启动失败: " + ex); }

            _web = new WebServerService();
            // Main 为 STAThread 且尚未进入消息循环，同步等待启动是安全的
            _web.StartAsync().GetAwaiter().GetResult();

            // 使用无参 Run()：Application.Run(Form) 会自动 Show 主窗体，
            // 覆盖托盘驻留逻辑。静态引用保持窗体与 NotifyIcon 存活。
            // --show 便于首次运行/验收时直接打开主窗口。
            var showUi = Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
            _home = new Home();
            if (showUi) _home.ShowMain(-1);

            try
            {
                Application.Run();
            }
            finally
            {
                _web?.StopAsync().GetAwaiter().GetResult();
                _ws?.Stop();
                PrintQueueService.Instance.Shutdown();
                _singleInstance?.ReleaseMutex();
            }
        }

        /// <summary>供托盘菜单「打开文档站点」使用。</summary>
        public static string DocsUrl => _web?.Endpoint ?? "http://127.0.0.1:19861";

        public static bool WebRunning => _web?.IsRunning ?? false;

        public static bool WsRunning => _ws != null;
    }
}

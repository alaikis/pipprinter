using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using DuoAssistant.Views.Pages;
using Newtonsoft.Json.Linq;

namespace DuoAssistant
{
    /// <summary>
    /// 主窗体：托盘常驻 + 右键菜单 + 六页 TabControl 面板。
    /// 最小化 / 关闭都收进托盘，只有菜单「退出」才真正结束进程。
    /// </summary>
    public partial class Home : Form
    {
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private TabControl _tabs;
        private ToolStripStatusLabel _status;
        private bool _allowClose;
        private bool _isShown;
        // 阻止 WinForms 框架在 Application.Run 时自动显示主窗体
        private bool _allowVisible;

        public Home()
        {
            InitializeComponent();
            Resize += OnResize;
            FormClosing += OnFormClosing;
            PrintQueueService.Instance.Changed += (s, e) => Ui.Safe(this, UpdateStatus);
            UpdateStatus();
        }

        private void BuildUi()
        {
            Font = new Font("Microsoft YaHei", 9F);
            Text = Brand.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 700);
            MinimumSize = new Size(900, 560);
            AutoScaleMode = AutoScaleMode.Font;
            // 不动态切换 ShowInTaskbar（切换会重建句柄导致托盘/显示状态异常）；
            // 窗体隐藏时本来就不会出现在任务栏。
            ShowInTaskbar = true;

            var icon = LoadIcon();
            if (icon != null) Icon = icon;

            BuildTabs();
            BuildStatusStrip();
            BuildTray(icon);

            // 启动即驻留托盘，不弹主窗口
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        private static Icon LoadIcon()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Resource", "bitbug_favicon.ico");
                if (File.Exists(path)) return new Icon(path);
            }
            catch (Exception ex) { Tools.Logger("图标加载失败，使用系统图标: " + ex.Message); }
            return SystemIcons.Application;
        }

        private void BuildTabs()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill, Font = Font, Padding = new Point(12, 6) };
            AddTab("打印", new PrintTab());
            AddTab("打印队列", new QueueTab());
            AddTab("打印机", new PrintersTab());
            AddTab("磁盘清理", new DiskTab());
            AddTab("设置", new SettingsTab());
            AddTab("日志", new LogsTab());
            // 默认展示「打印队列」页
            _tabs.SelectedIndex = 1;
            Controls.Add(_tabs);
        }

        private void AddTab(string name, Control page)
        {
            var tab = new TabPage(name) { Padding = new Padding(0), BackColor = SystemColors.Window };
            page.Dock = DockStyle.Fill;
            tab.Controls.Add(page);
            _tabs.TabPages.Add(tab);
        }

        private void BuildStatusStrip()
        {
            _status = new ToolStripStatusLabel { Text = "就绪", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var strip = new StatusStrip { SizingGrip = false, Font = Font };
            strip.Items.Add(_status);
            Controls.Add(strip);
        }

        private void BuildTray(Icon icon)
        {
            _menu = new ContextMenuStrip { Font = Font, RenderMode = ToolStripRenderMode.System };
            components.Add(_menu);

            AddMenuItem("打开主窗口", (s, e) => ShowMain(-1), true);
            AddMenuItem("快速打印…", (s, e) => QuickPrint());
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("打印队列", (s, e) => ShowMain(1));
            AddMenuItem("打印机列表", (s, e) => ShowMain(2));
            AddMenuItem("磁盘清理", (s, e) => ShowMain(3));
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("打开文档站点", (s, e) => OpenDocs());
            AddMenuItem("查看本机指纹", (s, e) => ShowMachineFingerprint());
            AddMenuItem("检查更新", (s, e) => CheckForUpdate());
            AddMenuItem("设置", (s, e) => ShowMain(4));
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("退出", (s, e) => ExitApp());

            _tray = new NotifyIcon(components)
            {
                Icon = icon ?? SystemIcons.Application,
                Text = Brand.Title,
                Visible = true,
                ContextMenuStrip = _menu
            };
            // 双击托盘图标始终显示主窗口（不切换隐藏），最小化/关闭窗口才收回托盘
            _tray.MouseDoubleClick += (s, e) => ShowMain(-1);
            _tray.BalloonTipTitle = Brand.Title;
            _tray.BalloonTipText = "已启动，右键托盘图标可管理打印任务与文档服务。";
            _tray.ShowBalloonTip(2500);
        }

        private void AddMenuItem(string text, EventHandler handler, bool bold = false)
        {
            var item = new ToolStripMenuItem(text)
            {
                Font = bold ? new Font(Font, FontStyle.Bold) : Font
            };
            item.Click += handler;
            _menu.Items.Add(item);
        }

        // ------------------------------------------------------------ 行为

        /// <summary>
        /// 拦截框架自动 Show：只有 ShowMain / --show 才允许窗口真正可见。
        /// </summary>
        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible) value = false;
            base.SetVisibleCore(value);
        }

        /// <summary>index &lt; 0 时保持当前页签（启动时默认为打印队列）。</summary>
        public void ShowMain(int index)
        {
            if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
            _allowVisible = true;
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            _isShown = true;
            if (_tabs.SelectedTab?.Controls[0] is IRefreshable page) page.RefreshData();
        }

        public void HideMain()
        {
            _allowVisible = false;
            Hide();
            WindowState = FormWindowState.Minimized;
            _isShown = false;
        }

        private void QuickPrint()
        {
            using (var dlg = new OpenFileDialog { Filter = PrintFileTypes.Filter, CheckFileExists = true })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var payload = new JObject { ["path"] = dlg.FileName }.ToString();
                var res = JObject.Parse(CommandDispatcher.Execute("QueuePrint", payload));
                var ok = res["code"]?.Value<int>() == 200;
                _tray.ShowBalloonTip(2500, "快速打印",
                    ok ? "已加入打印队列：" + Path.GetFileName(dlg.FileName)
                       : "失败：" + res["error"], ok ? ToolTipIcon.Info : ToolTipIcon.Error);
            }
        }

        private void OpenDocs()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开文档站点：" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowMachineFingerprint()
        {
            try
            {
                var res = JObject.Parse(CommandDispatcher.Execute("GetMachineFingerprint", "{}"));
                var data = res["data"] as JObject;
                var msg = "本机指纹（跨浏览器/会话稳定）\n" +
                          "machineId: " + data?["machineId"] + "\n" +
                          "machineGuid: " + data?["machineGuid"] + "\n" +
                          "volumeSerial: " + data?["volumeSerial"] + "\n" +
                          "OS: " + data?["os"] + "\n" +
                          "user: " + data?["user"] + "\n" +
                          "app: " + data?["app"] + " v" + data?["version"];
                MessageBox.Show(msg, Brand.NameCn + " · 本机指纹",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取本机指纹失败：" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CheckForUpdate()
        {
            var res = JObject.Parse(CommandDispatcher.Execute("CheckUpdate", "{}"));
            var data = res["data"] as JObject;
            if (res["code"]?.Value<int>() != 200)
            {
                MessageBox.Show("检查更新失败：" + res["error"], "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (data?["hasUpdate"]?.Value<bool>() == true)
            {
                var ok = MessageBox.Show("发现新版本 " + data["remote"] + "（当前 " + data["current"] + "）。是否立即更新？",
                    "自动更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (ok == DialogResult.Yes)
                {
                    var applyRes = JObject.Parse(CommandDispatcher.Execute("ApplyUpdate", "{}"));
                    if (applyRes["code"]?.Value<int>() == 200)
                    {
                        var exePath = applyRes["data"]?["exePath"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); } catch { }
                        }
                        Application.Exit();
                    }
                    else
                    {
                        MessageBox.Show("更新失败：" + applyRes["error"], "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            else
            {
                MessageBox.Show("已是最新版本 " + data?["current"], "自动更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ExitApp()
        {
            _allowClose = true;
            _tray.Visible = false;
            Close();
            Application.Exit();
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && _isShown) HideMain();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideMain();
        }

        private void UpdateStatus()
        {
            if (_status == null) return;
            var s = SettingsStore.Instance.Current;
            var q = JObject.FromObject(PrintQueueService.Instance.Summary());
            _status.Text = string.Format(
                "WS {0}:{1} {2}   |   Web 文档 {3}   |   队列 等待 {4} / 打印中 {5} / 完成 {6} / 失败 {7}",
                s.WsHost, s.WsPort, Program.WsRunning ? "●" : "○",
                Program.WebRunning ? Program.DocsUrl : "未运行",
                q["pending"], q["printing"], q["done"], q["failed"]);
            Text = Brand.Title + (q["pending"]?.Value<int>() > 0 ? "  [队列待处理]" : "");
        }
    }
}

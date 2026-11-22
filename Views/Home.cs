// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
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
        // 上次观察到的等待任务数，用于检测「新任务入队」并自动弹出打印队列窗口
        private int _lastPendingCount;
        // 自动更新检查发现的版本；气泡点击时据此走更新确认流程
        private UpdateInfo _pendingAutoUpdate;
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
            Font = Theme.BodyFont;
            Text = Brand.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 700);
            MinimumSize = new Size(900, 560);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Theme.Bg;
            // 不动态切换 ShowInTaskbar（切换会重建句柄导致托盘/显示状态异常）；
            // 窗体隐藏时本来就不会出现在任务栏。
            ShowInTaskbar = true;

            var icon = LoadIcon("app.ico");
            if (icon != null) Icon = icon;

            BuildTabs();
            BuildStatusStrip();
            BuildTray(LoadIcon("tray.ico"));
            Theme.Apply(this);

            // 启动即驻留托盘，不弹主窗口
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        /// <summary>
        /// 加载图标：优先 Resource/tray.ico；若文件缺失或 ico 数据无法被 GDI+ 解析，
        /// 回退到 exe 内嵌的 app.ico 资源；两者都失败时使用系统图标。
        /// 关键：NotifyIcon.Icon 必须持有非 null 的 Icon 实例，null 会导致 Windows
        /// Shell 在任务栏渲染出"空"图标（托盘图标不显示）。
        /// </summary>
        private static Icon LoadIcon(string file)
        {
            // 第一层：磁盘上的 tray.ico / app.ico（发布时由 csproj 复制出来）
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resource", file);
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var icon = new Icon(stream);
                    if (icon != null && icon.ToBitmap().Width > 0)
                        return icon;
                }
            }
            catch (Exception ex)
            {
                Tools.Logger($"Resource/{file} 加载失败，回退 exe 内嵌图标: {ex.Message}");
            }

            // 第二层：exe 内嵌资源（csproj <ApplicationIcon>Resource\app.ico</ApplicationIcon>）
            try
            {
                var exe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    var embedded = Icon.ExtractAssociatedIcon(exe);
                    if (embedded != null && embedded.ToBitmap().Width > 0)
                        return embedded;
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("exe 内嵌图标提取失败，回退系统图标: " + ex.Message);
            }

            // 第三层：系统默认图标（绝不返回 null）
            Tools.Logger("图标加载全部失败，使用 SystemIcons.Application");
            return SystemIcons.Application;
        }

        private void BuildTabs()
        {
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill, Font = Theme.BodyFont,
                Padding = new Point(12, 6), BackColor = Theme.Card,
                DrawMode = TabDrawMode.OwnerDrawFixed
            };
            // 胶囊页签：选中浅红底 + 荔枝红粗体，未选中灰字（Wise 风格极简页签）
            _tabs.DrawItem += (s, e) =>
            {
                var tab = _tabs.TabPages[e.Index];
                var bounds = e.Bounds;
                bounds.Inflate(-2, -2);
                bool selected = _tabs.SelectedIndex == e.Index;

                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (selected)
                {
                    using (var path = Theme.RoundedRect(bounds, Theme.Radius))
                    using (var brush = new SolidBrush(Theme.PrimarySoft))
                        e.Graphics.FillPath(brush, path);
                }
                using (var font = new Font("Microsoft YaHei", 9F,
                    selected ? FontStyle.Bold : FontStyle.Regular))
                using (var brush = new SolidBrush(selected ? Theme.PrimaryDark : Theme.Muted))
                {
                    var sf = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        Alignment = StringAlignment.Center
                    };
                    e.Graphics.DrawString(tab.Text, font, brush, bounds, sf);
                }
            };
            AddTab(L10n.T("打印"), new PrintTab());
            AddTab(L10n.T("打印队列"), new QueueTab());
            AddTab(L10n.T("打印管理"), new ManageTab());
            AddTab(L10n.T("打印机"), new PrintersTab());
            AddTab(L10n.T("磁盘清理"), new DiskTab());
            AddTab(L10n.T("设置"), new SettingsTab());
            AddTab(L10n.T("日志"), new LogsTab());
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
            _status = new ToolStripStatusLabel { Text = L10n.T("就绪"), Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var strip = new StatusStrip { SizingGrip = false, Font = Theme.BodyFont, BackColor = Theme.Card, ForeColor = Theme.Muted };
            strip.Renderer = new Theme.ToolStripRenderer();
            strip.Items.Add(_status);
            Controls.Add(strip);
        }

        private void BuildTray(Icon icon)
        {
            _menu = new ContextMenuStrip
            {
                Font = Theme.BodyFont, RenderMode = ToolStripRenderMode.Professional,
                BackColor = Theme.Card, ForeColor = Theme.Text
            };
            _menu.Renderer = new Theme.ToolStripRenderer();
            components.Add(_menu);

            AddMenuItem(L10n.T("打开主窗口"), (s, e) => ShowMain(-1), true);
            AddMenuItem(L10n.T("快速打印…"), (s, e) => QuickPrint());
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(L10n.T("打印队列"), (s, e) => ShowMain(1));
            AddMenuItem(L10n.T("打印机列表"), (s, e) => ShowMain(2));
            AddMenuItem(L10n.T("磁盘清理"), (s, e) => ShowMain(3));
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(L10n.T("打开文档站点"), (s, e) => OpenDocs());
            AddMenuItem(L10n.T("查看本机指纹"), (s, e) => ShowMachineFingerprint());
            AddMenuItem(L10n.T("检查更新"), (s, e) => CheckForUpdate());
            AddMenuItem(L10n.T("设置"), (s, e) => ShowMain(4));
            _menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(L10n.T("退出"), (s, e) => ExitApp());

            _tray = new NotifyIcon(components)
            {
                Icon = icon ?? SystemIcons.Application,
                Text = Brand.Title,
                Visible = false,
                ContextMenuStrip = _menu
            };
            // 双击托盘图标始终显示主窗口（不切换隐藏），最小化/关闭窗口才收回托盘
            _tray.MouseDoubleClick += (s, e) => ShowMain(-1);
            // 点击「发现新版本」气泡直接走更新确认流程；其他气泡（启动/快速打印/队列）不触发
            _tray.BalloonTipClicked += (s, e) =>
            {
                var pending = _pendingAutoUpdate;
                _pendingAutoUpdate = null;
                if (pending != null) Ui.Safe(this, () => ConfirmAndApplyUpdate(pending));
            };
            _tray.BalloonTipTitle = Brand.Title;
            _tray.BalloonTipText = L10n.T("已启动，右键托盘图标可管理打印任务与文档服务。");

            // 关键：Windows Shell 对 NotifyIcon.Icon 的"先 Visible 后赋图标"存在已知时序缺陷——
            // 在 Visible=true 与图标句柄注册之间若 Shell 已完成首次绘制，图标会永久不显示。
            // 解决：先以 Visible=false 创建并赋值 Icon（此时 Shell 尚未注册 shell notification
            // icon），再显示并弹出气泡；强制 Shell 走"重新注册"路径，图标即可正常渲染。
            _tray.Visible = true;
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
                _pendingAutoUpdate = null;
                _tray.ShowBalloonTip(2500, L10n.T("快速打印"),
                    ok ? L10n.T("已加入打印队列：") + Path.GetFileName(dlg.FileName)
                       : L10n.T("失败：") + res["error"], ok ? ToolTipIcon.Info : ToolTipIcon.Error);
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
                MessageBox.Show(L10n.T("无法打开文档站点：") + ex.Message, L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowMachineFingerprint()
        {
            try
            {
                var res = JObject.Parse(CommandDispatcher.Execute("GetMachineFingerprint", "{}"));
                var data = res["data"] as JObject;
                var msg = L10n.T("本机指纹（跨浏览器/会话稳定）") + "\n" +
                          "machineId: " + data?["machineId"] + "\n" +
                          "machineGuid: " + data?["machineGuid"] + "\n" +
                          "volumeSerial: " + data?["volumeSerial"] + "\n" +
                          "OS: " + data?["os"] + "\n" +
                          "user: " + data?["user"] + "\n" +
                          "app: " + data?["app"] + " v" + data?["version"];
                MessageBox.Show(msg, Brand.NameCn + " · " + L10n.T("本机指纹"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L10n.T("获取本机指纹失败：") + ex.Message, L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 自动检查更新的回调入口（来自后台线程）。只做轻量提示，不打断用户：
        /// 弹托盘气泡，用户点击气泡才进入确认更新流程。
        /// </summary>
        public void OnAutoUpdateFound(UpdateInfo info)
        {
            if (info == null || !info.HasUpdate) return;
            Ui.Safe(this, () =>
            {
                _pendingAutoUpdate = info;
                try
                {
                    _tray?.ShowBalloonTip(8000, L10n.T("发现新版本"),
                        L10n.F("发现新版本 {0}（当前 {1}），点击此处查看更新说明并安装。", info.Remote, info.Current),
                        ToolTipIcon.Info);
                }
                catch (Exception ex) { Tools.Logger("自动更新提示失败: " + ex.Message); }
            });
        }

        /// <summary>气泡点击 / 手动检查命中新版本后的确认与安装流程。</summary>
        private void ConfirmAndApplyUpdate(UpdateInfo info)
        {
            var ok = MessageBox.Show(
                L10n.F("发现新版本 {0}（当前 {1}）。是否立即更新？", info.Remote, info.Current),
                L10n.T("自动更新"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ok != DialogResult.Yes) return;

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
                MessageBox.Show(L10n.T("更新失败：") + applyRes["error"], L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CheckForUpdate()
        {
            var res = JObject.Parse(CommandDispatcher.Execute("CheckUpdate", "{}"));
            var data = res["data"] as JObject;
            if (res["code"]?.Value<int>() != 200)
            {
                MessageBox.Show(L10n.T("检查更新失败：") + res["error"], L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (data?["hasUpdate"]?.Value<bool>() == true)
            {
                ConfirmAndApplyUpdate(new UpdateInfo
                {
                    HasUpdate = true,
                    Remote = data["remote"]?.ToString(),
                    Current = data["current"]?.ToString()
                });
            }
            else
            {
                MessageBox.Show(L10n.T("已是最新版本 ") + data?["current"], L10n.T("自动更新"),
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
            _status.Text = L10n.F(
                "WS {0}:{1}/ws {2}   |   Web 文档 {3}   |   队列 等待 {4} / 打印中 {5} / 完成 {6} / 失败 {7}",
                s.WebHost, s.WebPort, Program.WsRunning ? "●" : "○",
                Program.WebRunning ? Program.DocsUrl : L10n.T("未运行"),
                q["pending"], q["printing"], q["done"], q["failed"]);
            Text = Brand.Title + (q["pending"]?.Value<int>() > 0 ? L10n.T("  [队列待处理]") : "");

            // 新任务入队时自动弹出打印队列窗口（用户明确要求打印时能看到前台队列窗口）。
            // 只在等待数「增加」且主窗口当前隐藏时触发，避免打印中/完成态变化反复弹窗。
            var pending = q["pending"]?.Value<int>() ?? 0;
            if (pending > _lastPendingCount && !_isShown)
            {
                ShowMain(1);
                _pendingAutoUpdate = null;
                _tray?.ShowBalloonTip(2000, L10n.T("打印队列"),
                    L10n.F("新任务入队：{0} 个等待处理", pending), ToolTipIcon.Info);
            }
            _lastPendingCount = pending;
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「设置」页：端口、默认打印机、清理策略、开机自启、文档/帮助、本机指纹、自动更新。</summary>
    public class SettingsTab : UserControl, IRefreshable
    {
        private readonly TextBox _wsHost = Ui.Input(150);
        private readonly NumericUpDown _wsPort = Ui.Number(1, 65535, 19860, 100);
        private readonly TextBox _webHost = Ui.Input(150);
        private readonly NumericUpDown _webPort = Ui.Number(1, 65535, 19861, 100);
        private readonly ComboBox _defaultPrinter = Ui.Combo(260);
        private readonly ComboBox _logLevel = Ui.Combo(120);
        private readonly NumericUpDown _age = Ui.Number(0, 365, 7, 80);
        private readonly NumericUpDown _minSize = Ui.Number(0, 1024, 1, 80);
        private readonly CheckBox _autoStart = new CheckBox { Text = "开机自启", AutoSize = true, Font = Ui.LabelFont };
        private readonly CheckBox _recycle = new CheckBox { Text = "清理时移入回收站", AutoSize = true, Font = Ui.LabelFont };
        private readonly TextBox _whitelist = Ui.Input(520, true);
        private readonly TextBox _machineId = Ui.Input(520);
        private readonly TextBox _manifestUrl = Ui.Input(380);
        private readonly Label _updateStatus = Ui.Label("尚未检查更新");
        private readonly Label _status = Ui.Label("");

        public SettingsTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
            AutoScroll = true;
            BuildUi();
            RefreshData();
        }

        private void BuildUi()
        {
            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12),
                Font = Ui.LabelFont
            };

            root.Controls.Add(Ui.Label("服务与网络", true));
            root.Controls.Add(Row(Ui.Label("WebSocket 监听："), _wsHost, Ui.Label("端口："), _wsPort));
            root.Controls.Add(Row(Ui.Label("Web 文档监听："), _webHost, Ui.Label("端口："), _webPort));
            root.Controls.Add(Row(Ui.Label("默认打印机："), _defaultPrinter,
                Ui.Button("刷新", (s, e) => RefreshPrinters(), 70)));

            root.Controls.Add(Ui.Label("打印与日志", true));
            _logLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARN", "ERROR" });
            root.Controls.Add(Row(Ui.Label("日志级别："), _logLevel, _autoStart));

            root.Controls.Add(Ui.Label("磁盘清理", true));
            root.Controls.Add(Row(Ui.Label("保留天数："), _age, Ui.Label("最小文件(MB)："), _minSize, _recycle));
            root.Controls.Add(Ui.Label("白名单目录（每行一个，支持环境变量）："));
            _whitelist.Height = 110;
            _whitelist.Dock = DockStyle.None;
            root.Controls.Add(_whitelist);

            root.Controls.Add(Ui.Label("文档与帮助", true));
            root.Controls.Add(Row(
                Ui.Button("📖 打开文档站点", (s, e) => OpenDocs(), 140),
                Ui.Button("📖 查看自动更新", (s, e) => OpenUpdateDocs(), 140),
                Ui.Label("默认浏览器打开内嵌文档站（端口 19861）")));

            root.Controls.Add(Ui.Label("本机指纹", true));
            _machineId.ReadOnly = true;
            _machineId.BackColor = SystemColors.Control;
            root.Controls.Add(Row(Ui.Label("machineId："), _machineId,
                Ui.Button("复制", (s, e) => CopyMachineId(), 70)));
            root.Controls.Add(Ui.Label("跨浏览器 / 会话稳定的本机标识；作为打印习惯的 machine-key", false));

            root.Controls.Add(Ui.Label("自动更新（Gitee）", true));
            root.Controls.Add(Row(Ui.Label("更新清单 URL："), _manifestUrl));
            root.Controls.Add(Row(Ui.Button("检查更新", (s, e) => CheckUpdate()), _updateStatus));

            root.Controls.Add(Row(
                Ui.Button("保存设置", (s, e) => Save(), 120),
                Ui.Button("重新载入", (s, e) => RefreshData(), 100),
                Ui.Button("取消开机自启", (s, e) => DisableAutoStart(), 130),
                _status));
            Controls.Add(root);
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 2, 0, 8),
                Font = Ui.LabelFont
            };
            foreach (var c in controls) p.Controls.Add(c);
            return p;
        }

        public void RefreshData()
        {
            var s = SettingsStore.Instance.Current;
            _wsHost.Text = s.WsHost;
            _wsPort.Value = s.WsPort;
            _webHost.Text = s.WebHost;
            _webPort.Value = s.WebPort;
            _logLevel.SelectedItem = s.LogLevel ?? "DEBUG";
            if (_logLevel.SelectedIndex < 0) _logLevel.SelectedIndex = 0;
            _age.Value = Math.Min(_age.Maximum, s.CleanMaxAgeDays);
            _minSize.Value = Math.Min(_minSize.Maximum, s.CleanMinSizeMB);
            _autoStart.Checked = new BaseServices().IsAutoStartEnabled();
            _recycle.Checked = s.UseRecycleBin;
            _whitelist.Text = string.Join(Environment.NewLine, s.CleanWhitelist ?? new System.Collections.Generic.List<string>());
            _manifestUrl.Text = s.UpdateManifestUrl;
            _machineId.Text = s.MachineId;
            _updateStatus.Text = "尚未检查更新（当前 " + Brand.Version + "）";
            _status.Text = Program.WebRunning ? "Web 服务运行中：" + Program.DocsUrl : "Web 服务未运行";
            RefreshPrinters();
        }

        private void RefreshPrinters()
        {
            _defaultPrinter.Items.Clear();
            _defaultPrinter.Items.Add("(系统默认)");
            foreach (var n in new PrinterService().GetNames()) _defaultPrinter.Items.Add(n);
            var preferred = SettingsStore.Instance.Current.DefaultPrinter;
            if (!string.IsNullOrWhiteSpace(preferred) && _defaultPrinter.Items.Contains(preferred))
                _defaultPrinter.SelectedItem = preferred;
            else _defaultPrinter.SelectedIndex = 0;
        }

        private void Save()
        {
            var list = new JArray();
            foreach (var line in _whitelist.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());

            var payload = new JObject
            {
                ["wsHost"] = _wsHost.Text.Trim(),
                ["wsPort"] = (int)_wsPort.Value,
                ["webHost"] = _webHost.Text.Trim(),
                ["webPort"] = (int)_webPort.Value,
                ["defaultPrinter"] = _defaultPrinter.SelectedIndex == 0 ? "" : _defaultPrinter.SelectedItem?.ToString() ?? "",
                ["logLevel"] = _logLevel.SelectedItem?.ToString() ?? "DEBUG",
                ["cleanMaxAgeDays"] = (int)_age.Value,
                ["cleanMinSizeMB"] = (int)_minSize.Value,
                ["autoStart"] = _autoStart.Checked,
                ["useRecycleBin"] = _recycle.Checked,
                ["cleanWhitelist"] = list,
                ["updateManifestUrl"] = _manifestUrl.Text.Trim()
            };
            var res = JObject.Parse(CommandDispatcher.Execute("SaveSettings", payload.ToString()));
            CommandDispatcher.ResetCache();
            if (_autoStart.Checked) new BaseServices().StartWhenSystemStart();
            else new BaseServices().DisableAutoStart();
            RefreshData();
            MessageBox.Show(res["error"]?.ToString(), "设置", MessageBoxButtons.OK,
                res["code"]?.Value<int>() == 200 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DisableAutoStart()
        {
            new BaseServices().DisableAutoStart();
            _autoStart.Checked = false;
            _status.Text = "已取消开机自启（需保存设置以持久化）";
        }

        // ------------------------------------------------------------ 文档 / 帮助 / 更新

        private void OpenDocs()
        {
            if (!Program.WebRunning)
            {
                MessageBox.Show("Web 服务未运行。请保存设置后重启应用。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start(new ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("无法打开文档站点：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void OpenUpdateDocs()
        {
            if (!Program.WebRunning)
            {
                MessageBox.Show("Web 服务未运行。请保存设置后重启应用。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start(new ProcessStartInfo(Program.DocsUrl + "/dev.html#自动更新") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("无法打开文档：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void CopyMachineId()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_machineId.Text))
                {
                    _machineId.Text = MachineFingerprint.Get();
                }
                Clipboard.SetText(_machineId.Text);
                _status.Text = "machineId 已复制";
            }
            catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
        }

        private void CheckUpdate()
        {
            _updateStatus.Text = "正在检查更新…";
            Application.DoEvents();
            try
            {
                var res = JObject.Parse(CommandDispatcher.Execute("CheckUpdate", "{}"));
                var data = res["data"] as JObject;
                if (res["code"]?.Value<int>() != 200)
                {
                    _updateStatus.Text = "检查失败：" + res["error"];
                    return;
                }
                if (data?["hasUpdate"]?.Value<bool>() == true)
                {
                    _updateStatus.Text = "发现新版本 " + data["remote"] + "（当前 " + data["current"] + "）";
                    var apply = MessageBox.Show(
                        "发现新版本 " + data["remote"] + "。是否立即下载并安装？\n（旧版将备份到 PipPrinter.exe.bak-* 目录，可手动还原）",
                        "自动更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (apply == DialogResult.Yes)
                    {
                        _updateStatus.Text = "正在下载并安装…";
                        var applyRes = JObject.Parse(CommandDispatcher.Execute("ApplyUpdate", "{}"));
                        if (applyRes["code"]?.Value<int>() == 200)
                        {
                            var restart = MessageBox.Show(
                                "更新已安装到 " + applyRes["data"]?["exePath"] +
                                "，旧版备份到 " + applyRes["data"]?["backupPath"] +
                                "。\n是否立即重启？",
                                "重启", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                            if (restart == DialogResult.Yes)
                            {
                                var exePath = applyRes["data"]?["exePath"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(exePath))
                                {
                                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                                }
                                Application.Exit();
                            }
                        }
                        else
                        {
                            _updateStatus.Text = "安装失败：" + applyRes["error"];
                        }
                    }
                    else
                    {
                        _updateStatus.Text = "已跳过本次更新";
                    }
                }
                else
                {
                    _updateStatus.Text = "已是最新版本 " + data?["current"];
                }
            }
            catch (Exception ex)
            {
                _updateStatus.Text = "检查更新异常：" + ex.Message;
            }
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;
using DuoAssistant.Services;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「设置」页：端口、默认打印机、清理策略、开机自启。</summary>
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
            root.Controls.Add(Row(Ui.Label("WebSocket 监听："), _wsHost, Ui.Label("端口："), _wsPort,
                Ui.Button("打开文档站点", (s, e) => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true }), 130)));
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
                ["cleanWhitelist"] = list
            };
            var res = JObject.Parse(CommandDispatcher.Execute("SaveSettings", payload.ToString()));
            CommandDispatcher.ResetCache();
            if (_autoStart.Checked) new BaseServices().StartWhenSystemStart();
            else new BaseServices().DisableAutoStart();
            MessageBox.Show(res["error"]?.ToString(), "设置", MessageBoxButtons.OK,
                res["code"]?.Value<int>() == 200 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DisableAutoStart()
        {
            new BaseServices().DisableAutoStart();
            _autoStart.Checked = false;
            _status.Text = "已取消开机自启（需保存设置以持久化）";
        }
    }
}

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「设置」页：语言、端口、默认打印机、清理策略、开机自启、文档/帮助、本机指纹、自动更新。</summary>
    public class SettingsTab : UserControl, IRefreshable
    {
        private readonly TextBox _wsEndpoint = Ui.Input(280);
        private readonly TextBox _webHost = Ui.Input(150);
        private readonly NumericUpDown _webPort = Ui.Number(1, 65535, 19861, 100);
        private readonly ComboBox _defaultPrinter = Ui.Combo(260);
        private readonly ComboBox _logLevel = Ui.Combo(120);
        private readonly ComboBox _language = Ui.Combo(160);
        private readonly NumericUpDown _age = Ui.Number(0, 365, 7, 80);
        private readonly NumericUpDown _minSize = Ui.Number(0, 1024, 1, 80);
        private readonly NumericUpDown _tempAutoCleanDays = Ui.Number(0, 365, 7, 40);
        // 默认打印参数（API 未显式传参时回退到这些值）
        private readonly NumericUpDown _defCopies = Ui.Number(1, 99, 1, 60);
        private readonly ComboBox _defOrientation = Ui.Combo(110);
        private readonly ComboBox _defPaperSize = Ui.EditableCombo(110);
        private readonly ComboBox _defDuplex = Ui.Combo(120);
        private readonly NumericUpDown _defMargin = Ui.Number(0, 500, 50, 70);
        private readonly ComboBox _defPageOrder = Ui.Combo(120);
        private readonly CheckBox _autoStart = new CheckBox { Text = "", AutoSize = true, Font = Ui.LabelFont };
        private readonly CheckBox _recycle = new CheckBox { Text = "", AutoSize = true, Font = Ui.LabelFont };
        private readonly TextBox _whitelist = Ui.Input(520, true);
        private readonly TextBox _machineId = Ui.Input(520);
        private readonly TextBox _manifestUrl = Ui.Input(380);
        private readonly CheckBox _autoCheckUpdate = new CheckBox { Text = "", AutoSize = true, Font = Ui.LabelFont };
        private readonly NumericUpDown _autoCheckInterval = Ui.Number(1, 720, 6, 55);
        private readonly Label _updateStatus = Ui.Label("");
        private readonly Label _status = Ui.Label("");
        // AO（Active-Online Print）
        private readonly CheckBox _aoEnabled = new CheckBox { Text = "", AutoSize = true, Font = Ui.LabelFont };
        private readonly TextBox _aoRelayUrl = Ui.Input(280);
        private readonly TextBox _aoKey = Ui.Input(160);
        private readonly TextBox _aoDeviceName = Ui.Input(200);
        private readonly Label _aoStatus = Ui.Label("");
        private string _lastSavedLanguage;
        private bool _aoWasEnabled;

        public SettingsTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;
            AutoScroll = true;
            BuildUi();
            RefreshData();
        }

        private void BuildUi()
        {
            _autoStart.Text = L10n.T("开机自启");
            _recycle.Text = L10n.T("清理时移入回收站");
            _aoEnabled.Text = L10n.T("启用 AO 终端：");
            _autoCheckUpdate.Text = L10n.T("自动检查新版本");
            _updateStatus.Text = L10n.T("尚未检查更新");

            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12),
                Font = Ui.LabelFont
            };

            root.Controls.Add(Ui.Label(L10n.T("界面语言"), true));
            foreach (var code in L10n.Supported) _language.Items.Add(L10n.DisplayName(code));
            root.Controls.Add(Row(Ui.Label(L10n.T("界面语言") + ": "), _language,
                Ui.Label(L10n.DisplayName(L10n.Auto) + " → " + L10n.DisplayName(L10n.Detected))));

            root.Controls.Add(Ui.Label(L10n.T("服务与网络"), true));
            _wsEndpoint.ReadOnly = true;
            _wsEndpoint.BackColor = SystemColors.Control;
            root.Controls.Add(Row(Ui.Label(L10n.T("WebSocket 端点：")), _wsEndpoint,
                Ui.Label(L10n.T("与 Web 共用端口，无需单独配置"))));
            root.Controls.Add(Row(Ui.Label(L10n.T("Web 文档监听：")), _webHost, Ui.Label(L10n.T("端口：")), _webPort));
            root.Controls.Add(Row(Ui.Label(L10n.T("默认打印机：")), _defaultPrinter,
                Ui.Button(L10n.T("刷新"), (s, e) => RefreshPrinters(), 70)));

            root.Controls.Add(Ui.Label(L10n.T("打印与日志"), true));
            _logLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARN", "ERROR" });
            root.Controls.Add(Row(Ui.Label(L10n.T("日志级别：")), _logLevel, _autoStart));

            root.Controls.Add(Ui.Label(L10n.T("默认打印参数"), true));
            _defOrientation.Items.AddRange(new object[] { "portrait", "landscape" });
            _defDuplex.Items.AddRange(new object[] { "simplex", "duplexlong", "duplexshort" });
            _defPaperSize.Items.AddRange(new object[]
            {
                "", "A0", "A1", "A2", "A3", "A4", "A5", "letter", "legal", "tabloid", "statement"
            });
            _defPageOrder.Items.AddRange(new object[] { "", "fwd", "rev", "fwdcoll", "revcoll" });
            root.Controls.Add(Row(Ui.Label(L10n.T("默认份数：")), _defCopies,
                Ui.Label(L10n.T("方向：")), _defOrientation,
                Ui.Label(L10n.T("双面：")), _defDuplex));
            root.Controls.Add(Row(Ui.Label(L10n.T("默认纸张：")), _defPaperSize,
                Ui.Label(L10n.T("边距(1/100英寸)：")), _defMargin,
                Ui.Label(L10n.T("页序：")), _defPageOrder));
            root.Controls.Add(Ui.Label(L10n.T("以上为接口未显式传参时的默认值；留空表示跟随打印机 / 系统默认。"), false));

            root.Controls.Add(Ui.Label(L10n.T("磁盘清理"), true));
            root.Controls.Add(Row(Ui.Label(L10n.T("保留天数：")), _age, Ui.Label(L10n.T("最小文件(MB)：")), _minSize, _recycle));
            root.Controls.Add(Row(Ui.Label(L10n.T("启动时自动清理临时文件（天）：")), _tempAutoCleanDays,
                Ui.Label(L10n.T("仅应用 temp/ 目录；0 = 关闭。修改后下次启动生效"))));
            root.Controls.Add(Ui.Label(L10n.T("白名单目录（每行一个，支持环境变量）：")));
            _whitelist.Height = 110;
            _whitelist.Dock = DockStyle.None;
            root.Controls.Add(_whitelist);

            root.Controls.Add(Ui.Label(L10n.T("文档与帮助"), true));
            root.Controls.Add(Row(
                Ui.Button("📖 " + L10n.T("打开文档站点"), (s, e) => OpenDocs(), 140),
                Ui.Button("📖 " + L10n.T("查看自动更新"), (s, e) => OpenUpdateDocs(), 140),
                Ui.Label(L10n.T("默认浏览器打开内嵌文档站（端口 19861）"))));

            root.Controls.Add(Ui.Label(L10n.T("本机指纹"), true));
            _machineId.ReadOnly = true;
            _machineId.BackColor = SystemColors.Control;
            root.Controls.Add(Row(Ui.Label(L10n.T("machineId：")), _machineId,
                Ui.Button(L10n.T("复制"), (s, e) => CopyMachineId(), 70)));
            root.Controls.Add(Ui.Label(L10n.T("跨浏览器 / 会话稳定的本机标识；作为打印习惯的 machine-key"), false));

            root.Controls.Add(Ui.Label(L10n.T("主动在线打印（AO）"), true));
            root.Controls.Add(Row(_aoEnabled, _aoStatus));
            root.Controls.Add(Row(Ui.Label(L10n.T("中继地址：")), _aoRelayUrl));
            root.Controls.Add(Row(Ui.Label(L10n.T("共享密钥：")), _aoKey,
                Ui.Label(L10n.T("终端名称：")), _aoDeviceName));
            root.Controls.Add(Ui.Label(L10n.T("支持 ws:// / wss:// / http(s)://，自动换算为 ws；留空密钥则不校验"), false));
            root.Controls.Add(Ui.Label(L10n.T("启用后本终端主动连接中继服务器，接受广域网远程打印任务（命令走白名单，仅允许已配置的命令）"), false));

            root.Controls.Add(Ui.Label(L10n.T("自动更新（Gitee）"), true));
            root.Controls.Add(Row(Ui.Label(L10n.T("更新清单 URL：")), _manifestUrl));
            root.Controls.Add(Row(_autoCheckUpdate,
                Ui.Label(L10n.T("检查间隔（小时）：")), _autoCheckInterval));
            root.Controls.Add(Ui.Label(L10n.T("启用后：启动延迟约 30 秒检查一次，之后按间隔在后台检查；发现新版本仅托盘提示，不自动下载或打断操作。"), false));
            root.Controls.Add(Row(Ui.Button(L10n.T("检查更新"), (s, e) => CheckUpdate()), _updateStatus));

            root.Controls.Add(Row(
                Ui.Button(L10n.T("保存设置"), (s, e) => Save(), 120, primary: true),
                Ui.Button(L10n.T("重新载入"), (s, e) => RefreshData(), 100),
                Ui.Button(L10n.T("取消开机自启"), (s, e) => DisableAutoStart(), 130),
                _status));
            Controls.Add(root);

            // AO 连接状态实时刷新（在线/离线随长连接变化）
            var aoTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            aoTimer.Tick += (s, e) => RefreshAoStatus();
            aoTimer.Start();
            Disposed += (s, e) => aoTimer.Dispose();
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
            _wsEndpoint.Text = "ws://" + s.WebHost + ":" + s.WebPort + "/ws";
            _webHost.Text = s.WebHost;
            _webPort.Value = s.WebPort;
            _logLevel.SelectedItem = s.LogLevel ?? "DEBUG";
            if (_logLevel.SelectedIndex < 0) _logLevel.SelectedIndex = 0;
            var codes = new[] { L10n.Auto, L10n.ZhCn, L10n.En };
            var langIndex = System.Array.IndexOf(codes, (s.Language ?? L10n.Auto).Trim());
            _language.SelectedIndex = langIndex >= 0 ? langIndex : 0;
            // 默认打印参数
            _defCopies.Value = Math.Min(_defCopies.Maximum, Math.Max(1, s.DefaultCopies));
            _defOrientation.SelectedItem = string.IsNullOrWhiteSpace(s.DefaultOrientation) ? "portrait" : s.DefaultOrientation;
            if (_defOrientation.SelectedIndex < 0) _defOrientation.SelectedIndex = 0;
            _defDuplex.SelectedItem = string.IsNullOrWhiteSpace(s.DefaultDuplex) ? "simplex" : s.DefaultDuplex;
            if (_defDuplex.SelectedIndex < 0) _defDuplex.SelectedIndex = 0;
            _defPaperSize.Text = s.DefaultPaperSize ?? "";
            _defMargin.Value = Math.Min(_defMargin.Maximum, Math.Max(0, s.DefaultMargin));
            _defPageOrder.SelectedItem = s.DefaultPageOrder ?? "";
            if (_defPageOrder.SelectedIndex < 0) _defPageOrder.SelectedIndex = 0;

            _age.Value = Math.Min(_age.Maximum, s.CleanMaxAgeDays);
            _minSize.Value = Math.Min(_minSize.Maximum, s.CleanMinSizeMB);
            _tempAutoCleanDays.Value = Math.Min(_tempAutoCleanDays.Maximum,
                Math.Max(0, s.TempAutoCleanMaxAgeDays));
            _autoStart.Checked = new BaseServices().IsAutoStartEnabled();
            _recycle.Checked = s.UseRecycleBin;
            _whitelist.Text = string.Join(Environment.NewLine, s.CleanWhitelist ?? new System.Collections.Generic.List<string>());
            _manifestUrl.Text = s.UpdateManifestUrl;
            _autoCheckUpdate.Checked = s.AutoCheckUpdate;
            _autoCheckInterval.Value = Math.Min(_autoCheckInterval.Maximum,
                Math.Max(1, s.AutoCheckIntervalHours > 0 ? s.AutoCheckIntervalHours : 6));
            _machineId.Text = s.MachineId;
            _aoEnabled.Checked = s.AoEnabled;
            _aoWasEnabled = s.AoEnabled;
            _aoRelayUrl.Text = s.AoRelayUrl;
            _aoKey.Text = s.AoKey;
            _aoDeviceName.Text = s.AoDeviceName;
            RefreshAoStatus();
            _updateStatus.Text = L10n.F("尚未检查更新（当前 {0}）", Brand.Version);
            _status.Text = Program.WebRunning ? L10n.T("Web 服务运行中：") + Program.DocsUrl : L10n.T("Web 服务未运行");
            RefreshPrinters();
        }

        /// <summary>AO 连接状态：在线 / 离线 / 未启用。</summary>
        private void RefreshAoStatus()
        {
            var s = SettingsStore.Instance.Current;
            if (!s.AoEnabled)
            {
                _aoStatus.Text = L10n.T("未启用");
                return;
            }
            _aoStatus.Text = AoClientService.Instance.Online
                ? L10n.T("AO 客户端运行中：") + L10n.T("在线") + " · " + AoClientService.Instance.DeviceId
                : L10n.T("AO 客户端运行中：") + L10n.T("离线");
        }

        private void RefreshPrinters()
        {
            _defaultPrinter.Items.Clear();
            _defaultPrinter.Items.Add(L10n.T("(系统默认)"));
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

            var codes = new[] { L10n.Auto, L10n.ZhCn, L10n.En };
            var language = _language.SelectedIndex >= 0 && _language.SelectedIndex < codes.Length
                ? codes[_language.SelectedIndex] : L10n.Auto;

            var payload = new JObject
            {
                ["webHost"] = _webHost.Text.Trim(),
                ["webPort"] = (int)_webPort.Value,
                ["defaultPrinter"] = _defaultPrinter.SelectedIndex == 0 ? "" : _defaultPrinter.SelectedItem?.ToString() ?? "",
                // 默认打印参数
                ["defaultCopies"] = (int)_defCopies.Value,
                ["defaultOrientation"] = _defOrientation.SelectedItem?.ToString() ?? "portrait",
                ["defaultPaperSize"] = _defPaperSize.Text.Trim(),
                ["defaultDuplex"] = _defDuplex.SelectedItem?.ToString() ?? "simplex",
                ["defaultMargin"] = (int)_defMargin.Value,
                ["defaultPageOrder"] = _defPageOrder.SelectedItem?.ToString() ?? "",
                ["logLevel"] = _logLevel.SelectedItem?.ToString() ?? "DEBUG",
                ["cleanMaxAgeDays"] = (int)_age.Value,
                ["cleanMinSizeMB"] = (int)_minSize.Value,
                ["tempAutoCleanMaxAgeDays"] = (int)_tempAutoCleanDays.Value,
                ["autoStart"] = _autoStart.Checked,
                ["useRecycleBin"] = _recycle.Checked,
                ["cleanWhitelist"] = list,
                ["updateManifestUrl"] = _manifestUrl.Text.Trim(),
                ["autoCheckUpdate"] = _autoCheckUpdate.Checked,
                ["autoCheckIntervalHours"] = (int)_autoCheckInterval.Value,
                ["language"] = language,
                ["aoEnabled"] = _aoEnabled.Checked,
                ["aoRelayUrl"] = _aoRelayUrl.Text.Trim(),
                ["aoKey"] = _aoKey.Text.Trim(),
                ["aoDeviceName"] = _aoDeviceName.Text.Trim()
            };
            var langChanged = _lastSavedLanguage != null && _lastSavedLanguage != language;
            var res = JObject.Parse(CommandDispatcher.Execute("SaveSettings", payload.ToString()));
            CommandDispatcher.ResetCache();
            // 自动更新检查的开关/间隔立即生效，无需重启
            if (res["code"]?.Value<int>() == 200) AutoUpdateService.Instance.Reconfigure();
            // AO 启用状态变更立即生效，无需重启（Start 幂等，Stop 安全）
            if (_aoEnabled.Checked != _aoWasEnabled)
            {
                if (_aoEnabled.Checked) AoClientService.Instance.Start();
                else AoClientService.Instance.Stop();
            }
            if (_autoStart.Checked) new BaseServices().StartWhenSystemStart();
            else new BaseServices().DisableAutoStart();
            _lastSavedLanguage = language;
            RefreshData();
            var msg = res["error"]?.ToString();
            if (langChanged && !string.IsNullOrWhiteSpace(msg)) msg += "\n" + L10n.T("语言已保存，重启应用后生效。");
            else if (langChanged) msg = L10n.T("语言已保存，重启应用后生效。");
            MessageBox.Show(msg, L10n.T("设置"), MessageBoxButtons.OK,
                res["code"]?.Value<int>() == 200 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DisableAutoStart()
        {
            new BaseServices().DisableAutoStart();
            _autoStart.Checked = false;
            _status.Text = L10n.T("已取消开机自启（需保存设置以持久化）");
        }

        // ------------------------------------------------------------ 文档 / 帮助 / 更新

        private void OpenDocs()
        {
            if (!Program.WebRunning)
            {
                MessageBox.Show(L10n.T("Web 服务未运行。请保存设置后重启应用。"), L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start(new ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(L10n.T("无法打开文档站点：") + ex.Message, L10n.T("提示"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void OpenUpdateDocs()
        {
            if (!Program.WebRunning)
            {
                MessageBox.Show(L10n.T("Web 服务未运行。请保存设置后重启应用。"), L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start(new ProcessStartInfo(Program.DocsUrl + "/dev.html#自动更新") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(L10n.T("无法打开文档：") + ex.Message, L10n.T("提示"),
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
                _status.Text = L10n.T("machineId 已复制");
            }
            catch (Exception ex) { _status.Text = L10n.T("复制失败：") + ex.Message; }
        }

        private void CheckUpdate()
        {
            _updateStatus.Text = L10n.T("正在检查更新…");
            Application.DoEvents();
            try
            {
                var res = JObject.Parse(CommandDispatcher.Execute("CheckUpdate", "{}"));
                var data = res["data"] as JObject;
                if (res["code"]?.Value<int>() != 200)
                {
                    _updateStatus.Text = L10n.T("检查失败：") + res["error"];
                    return;
                }
                if (data?["hasUpdate"]?.Value<bool>() == true)
                {
                    _updateStatus.Text = L10n.F("发现新版本 {0}（当前 {1}）", data["remote"], data["current"]);
                    var apply = MessageBox.Show(
                        L10n.F("发现新版本 {0}。是否立即下载并安装？\n（旧程序文件会自动让位备份，可手动还原）", data["remote"]),
                        L10n.T("自动更新"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (apply == DialogResult.Yes)
                    {
                        _updateStatus.Text = L10n.T("正在下载并安装…");
                        var applyRes = JObject.Parse(CommandDispatcher.Execute("ApplyUpdate", "{}"));
                        if (applyRes["code"]?.Value<int>() == 200)
                        {
                            var restart = MessageBox.Show(
                                L10n.F("更新已安装到 {0}，旧版备份到 {1}。\n是否立即重启？",
                                    applyRes["data"]?["exePath"], applyRes["data"]?["backupPath"]),
                                L10n.T("重启"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
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
                            _updateStatus.Text = L10n.T("安装失败：") + applyRes["error"];
                        }
                    }
                    else
                    {
                        _updateStatus.Text = L10n.T("已跳过本次更新");
                    }
                }
                else
                {
                    _updateStatus.Text = L10n.F("已是最新版本 {0}", data?["current"]);
                }
            }
            catch (Exception ex)
            {
                _updateStatus.Text = L10n.T("检查更新异常：") + ex.Message;
            }
        }
    }
}

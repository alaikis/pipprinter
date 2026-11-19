// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「打印机」页：局域网扫描、本机打印机列表、驱动信息、默认切换、测试页。</summary>
    public class PrintersTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List(
            L10n.T("打印机名称"), L10n.T("驱动"), L10n.T("端口"), L10n.T("默认"),
            L10n.T("状态"), L10n.T("纸张规格"));
        private readonly ListView _scanList = Ui.List(
            L10n.T("来源"), L10n.T("名称"), L10n.T("地址"), L10n.T("端口"),
            L10n.T("协议"), L10n.T("型号"));
        private readonly Label _info = Ui.Label("", true);
        private readonly Label _scanInfo = Ui.Label("", true);
        private TextBox _subnetInput;
        private Button _scanButton;
        private volatile bool _scanning;

        public PrintersTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;
            _list.Columns[0].Width = 240;
            _list.Columns[1].Width = 200;
            _list.Columns[2].Width = 130;
            _list.Columns[3].Width = 70;
            _list.Columns[4].Width = 90;
            _list.Columns[5].Width = 260;
            _scanList.Columns[0].Width = 110;
            _scanList.Columns[1].Width = 260;
            _scanList.Columns[2].Width = 150;
            _scanList.Columns[3].Width = 90;
            _scanList.Columns[4].Width = 90;
            _scanList.Columns[5].Width = 220;
            _scanInfo.Dock = DockStyle.Top;

            _scanButton = Ui.Button(L10n.T("扫描局域网"), (s, e) => ScanLan(), 110, primary: true);
            // 网段输入框：留空时只做被动发现；填写后追加 active 端口探测。
            _subnetInput = Ui.Input(230);
            var subnetHint = Ui.Label(L10n.T("指定网段（可选）："));
            subnetHint.Padding = new Padding(0, 9, 2, 0);
            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新列表"), (s, e) => RefreshData(), 100),
                _scanButton,
                subnetHint,
                _subnetInput,
                Ui.Button(L10n.T("设为默认"), (s, e) => SetDefault(), 100),
                Ui.Button(L10n.T("打印测试页"), (s, e) => TestPage(), 110),
                Ui.Button(L10n.T("打印到此设备"), (s, e) => PrintToSelectedDevice(), 110),
                Ui.Button(L10n.T("打开文档"), (s, e) => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true }), 100),
                _info);

            // 扫描结果在上、本机列表在下；两者共用同一工具栏。
            var scanPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 4) };
            scanPanel.Controls.Add(_scanList);
            scanPanel.Controls.Add(_scanInfo);

            var localPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            localPanel.Controls.Add(_list);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 190,
                BackColor = Theme.Border
            };
            split.Panel1.BackColor = Theme.Card;
            split.Panel2.BackColor = Theme.Card;
            split.Panel1.Controls.Add(scanPanel);
            split.Panel2.Controls.Add(localPanel);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            root.Controls.Add(split);
            root.Controls.Add(bar);
            Controls.Add(root);

            _scanInfo.Text = L10n.T("扫描结果仅用于识别设备；如需使用请在 Windows 中添加为网络打印机并安装厂商驱动。");
            RefreshData();
        }

        public void RefreshData()
        {
            var json = CommandDispatcher.Execute("GetPrinterList", "{}");
            var data = JObject.Parse(json)["data"] as JObject;
            var detail = data?["detail"] as JArray;
            _list.BeginUpdate();
            _list.Items.Clear();
            if (detail != null)
            {
                foreach (var d in detail)
                {
                    var item = new ListViewItem(d["Name"]?.ToString() ?? "");
                    item.SubItems.Add(d["Driver"]?.ToString() ?? "");
                    item.SubItems.Add(d["Port"]?.ToString() ?? "");
                    item.SubItems.Add(d["IsDefault"]?.Value<bool>() == true ? L10n.T("★ 是") : "");
                    item.SubItems.Add(d["Status"]?.ToString() ?? "");
                    item.SubItems.Add(d["PaperSizes"]?.ToString() ?? "");
                    if (d["IsDefault"]?.Value<bool>() == true) item.ForeColor = Color.FromArgb(29, 78, 216);
                    _list.Items.Add(item);
                }
            }
            _list.EndUpdate();
            _info.Text = L10n.F("本机共 {0} 台打印机 · 当前默认：", _list.Items.Count) +
                         (data?["primary"]?.ToString() ?? L10n.T("(无)"));
        }

        /// <summary>异步扫描局域网，避免网络发现阻塞 UI 线程。结果仅用于识别，不安装驱动。</summary>
        private void ScanLan()
        {
            if (_scanning) return;
            _scanning = true;
            _scanButton.Enabled = false;
            _scanButton.Text = L10n.T("扫描中…");
            _scanInfo.Text = L10n.T("扫描中…");

            // 在 UI 线程读取输入，构造参数：填了网段才追加 active 方式（默认不探测）。
            var subnets = (_subnetInput.Text ?? "").Trim();
            var body = new JObject { ["timeoutMs"] = 1500 };
            var methods = string.IsNullOrEmpty(subnets)
                ? "installed,ssdp,mdns"
                : "installed,ssdp,mdns,active";
            body["methods"] = methods;
            if (!string.IsNullOrEmpty(subnets)) body["subnets"] = subnets;
            var args = body.ToString();

            Task.Run(() =>
            {
                try
                {
                    return CommandDispatcher.Execute("ScanLanPrinters", args);
                }
                catch (Exception ex)
                {
                    return JObject.Parse(Result.error(ex.Message, cmd: "ScanLanPrinters")).ToString();
                }
            }).ContinueWith(t =>
            {
                Ui.Safe(this, () =>
                {
                    try { FillScan(t.Result); }
                    catch (Exception ex) { _scanInfo.Text = L10n.T("失败") + "：" + ex.Message; }
                    finally
                    {
                        _scanning = false;
                        _scanButton.Enabled = true;
                        _scanButton.Text = L10n.T("扫描局域网");
                    }
                });
            }, TaskScheduler.Default);
        }

        private void FillScan(string json)
        {
            var root = JObject.Parse(json);
            if (root["code"]?.Value<int>() != 200)
            {
                _scanInfo.Text = L10n.T("失败") + "：" + (root["error"]?.ToString() ?? "");
                return;
            }
            var data = root["data"] as JObject;
            var installed = data?["installedNetwork"] as JArray ?? new JArray();
            var discovered = data?["discovered"] as JArray ?? new JArray();
            var elapsed = data?["elapsedMs"]?.Value<int>() ?? 0;

            _scanList.BeginUpdate();
            _scanList.Items.Clear();
            foreach (var p in installed)
            {
                var item = new ListViewItem(L10n.T("本机网络打印机"));
                item.SubItems.Add(p["name"]?.ToString() ?? "");
                item.SubItems.Add(p["port"]?.ToString() ?? "");
                item.SubItems.Add("");
                item.SubItems.Add("GDI");
                item.SubItems.Add(p["driver"]?.ToString() ?? "");
                item.ForeColor = Theme.Success;
                _scanList.Items.Add(item);
            }
            foreach (var d in discovered)
            {
                var source = d["source"]?.ToString() ?? "";
                if (string.Equals(source, "active", StringComparison.OrdinalIgnoreCase))
                    source = L10n.T("端口探测");
                var item = new ListViewItem(source);
                item.SubItems.Add(d["name"]?.ToString() ?? "");
                item.SubItems.Add(d["address"]?.ToString() ?? "");
                item.SubItems.Add(d["port"]?.Value<int>() > 0 ? d["port"].ToString() : "");
                item.SubItems.Add(d["protocol"]?.ToString() ?? "");
                item.SubItems.Add(d["model"]?.ToString() ?? "");
                var addr = d["address"]?.ToString() ?? "";
                var port = d["port"]?.Value<int>() ?? 0;
                item.Tag = (!string.IsNullOrEmpty(addr) && port > 0) ? $"{addr}:{port}" : null;
                _scanList.Items.Add(item);
            }
            _scanList.EndUpdate();

            _scanInfo.Text = L10n.F("扫描完成：本机网络打印机 {0} 台 · 网络发现 {1} 台 · 耗时 {2} 毫秒",
                installed.Count, discovered.Count, elapsed);
            var probed = data?["probedHosts"]?.Value<int>() ?? 0;
            if (probed > 0)
            {
                _scanInfo.Text += " · " + L10n.F("已探测 {0} 个地址", probed);
                var skipped = data?["skippedHosts"]?.Value<int>() ?? 0;
                if (skipped > 0)
                    _scanInfo.Text += " · " + L10n.F("超出上限跳过 {0} 个", skipped);
            }
            if (installed.Count == 0 && discovered.Count == 0)
                _scanInfo.Text += " · " + L10n.T("尚未发现网络打印机。请确认目标设备已联网、已开机并开启了网络发现。");
        }

        private string SelectedName()
        {
            return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Text : null;
        }

        private void SetDefault()
        {
            var name = SelectedName();
            if (string.IsNullOrEmpty(name)) return;
            var res = JObject.Parse(CommandDispatcher.Execute("SetDefaultPrinter",
                new JObject { ["printer"] = name }.ToString()));
            if (res["code"]?.Value<int>() != 200)
                MessageBox.Show(res["error"]?.ToString(), L10n.T("失败"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshData();
        }

        private void TestPage()
        {
            var name = SelectedName();
            var res = JObject.Parse(CommandDispatcher.Execute("PrintTestPage",
                new JObject { ["printer"] = name ?? "" }.ToString()));
            MessageBox.Show(res["error"]?.ToString() ?? L10n.T("已发送测试页"), L10n.T("测试页"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshData();
        }

        /// <summary>
        /// 把文件直发选中扫描出的网络打印机（host:port）。不依赖 Windows 驱动：
        /// 走网络通道（631 走 IPP、9100 走 RAW），文件先渲染成 PDF 再发送。
        /// </summary>
        private void PrintToSelectedDevice()
        {
            var item = _scanList.SelectedItems.Count > 0 ? _scanList.SelectedItems[0] : null;
            var target = item?.Tag as string;
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show(L10n.T("请先在扫描结果中选中一台带地址的网络打印机。"),
                    L10n.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = L10n.T("选择要打印到此设备的文件");
                dlg.Filter = "可打印文件|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.txt;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.rtf;*.svg|所有文件|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var args = new JObject
                {
                    ["printer"] = target,
                    ["target"] = dlg.FileName,
                    ["copies"] = 1
                }.ToString();
                var json = CommandDispatcher.Execute("PrintFile", args);
                var res = JObject.Parse(json);
                if (res["code"]?.Value<int>() != 200)
                {
                    MessageBox.Show(res["error"]?.ToString(), L10n.T("发送失败"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var engine = res["data"]?["engine"]?.ToString() ?? "";
                var msg = res["data"]?["message"]?.ToString() ?? L10n.T("已发送");
                MessageBox.Show($"{L10n.T("已发送到此设备")} {target}\n{engine}\n{msg}",
                    L10n.T("打印到此设备"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}

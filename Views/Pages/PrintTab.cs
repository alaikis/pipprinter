// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「打印」页：选择文件/URL、打印机与打印参数，立即打印或加入队列。</summary>
    public class PrintTab : UserControl, IRefreshable
    {
        private readonly TextBox _path = Ui.Input(360);
        private readonly ComboBox _printers = Ui.Combo(220);
        private readonly NumericUpDown _copies = Ui.Number(1, 99, 1);
        private readonly ComboBox _fit = Ui.Combo(110);
        private readonly ComboBox _scale = Ui.Combo(110);
        private readonly ComboBox _paper = Ui.EditableCombo(100);
        private readonly TextBox _pages = Ui.Input(90);
        private readonly ComboBox _duplex = Ui.Combo(120);
        private readonly NumericUpDown _margin = Ui.Number(0, 500, 50, 70);
        private readonly ComboBox _pageOrder = Ui.Combo(100);
        private readonly TextBox _output = Ui.Input(0, true);
        private readonly Label _hint = Ui.Label("");

        public PrintTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;
            BuildUi();
            RefreshData();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var row1 = Ui.Toolbar(
                Ui.Label(L10n.T("文件 / URL：")), _path,
                Ui.Button(L10n.T("浏览…"), (s, e) => Browse(), 80),
                Ui.Label(L10n.T("打印机：")), _printers,
                Ui.Button(L10n.T("刷新"), (s, e) => RefreshPrinters(), 70));
            root.Controls.Add(row1, 0, 0);

            _fit.Items.AddRange(new object[] { "fit", "shrink", "noscale", "compat" });
            _fit.SelectedIndex = 0;
            _scale.Items.AddRange(new object[] { "portrait", "landscape" });
            _scale.SelectedIndex = 0;
            // 纸张预设：auto 按页面自身尺寸；A0/A1 由服务层换算为 SumatraPDF 自定义尺寸；
            // 其余为 SumatraPDF 标准名；下拉可直接键入驱动名或 "210mm x 297mm" 自定义值
            _paper.Items.AddRange(new object[]
            {
                "auto", "A0", "A1", "A2", "A3", "A4", "A5",
                "letter", "legal", "tabloid", "statement"
            });
            _paper.SelectedIndex = _paper.Items.IndexOf("A4");
            _pages.Text = "1-";
            var row2 = Ui.Toolbar(
                Ui.Label(L10n.T("份数：")), _copies,
                Ui.Label(L10n.T("缩放：")), _fit,
                Ui.Label(L10n.T("方向：")), _scale,
                Ui.Label(L10n.T("纸张：")), _paper,
                Ui.Label(L10n.T("页码：")), _pages);
            root.Controls.Add(row2, 0, 1);

            // 双面 / 边距 / 页序：与「设置 → 默认打印参数」同构；留空表示跟随打印机/系统默认
            var s0 = SettingsStore.Instance.Current;
            _duplex.Items.AddRange(new object[] { "simplex", "duplexlong", "duplexshort" });
            _duplex.SelectedItem = string.IsNullOrWhiteSpace(s0.DefaultDuplex) ? "simplex" : s0.DefaultDuplex;
            if (_duplex.SelectedIndex < 0) _duplex.SelectedIndex = 0;
            _margin.Value = Math.Min(_margin.Maximum, Math.Max(0, s0.DefaultMargin));
            _pageOrder.Items.AddRange(new object[] { "", "fwd", "rev", "fwdcoll", "revcoll" });
            _pageOrder.SelectedItem = s0.DefaultPageOrder ?? "";
            if (_pageOrder.SelectedIndex < 0) _pageOrder.SelectedIndex = 0;
            var row2b = Ui.Toolbar(
                Ui.Label(L10n.T("双面：")), _duplex,
                Ui.Label(L10n.T("边距(1/100英寸)：")), _margin,
                Ui.Label(L10n.T("页序：")), _pageOrder);
            root.Controls.Add(row2b, 0, 2);

            var row3 = Ui.Toolbar(
                Ui.Button(L10n.T("立即打印"), (s, e) => Run("PrintFile"), 120, primary: true),
                Ui.Button(L10n.T("加入队列"), (s, e) => Run("QueuePrint"), 120),
                Ui.Button(L10n.T("打印测试页"), (s, e) => TestPage(), 120),
                Ui.Button(L10n.T("清空输出"), (s, e) => _output.Clear(), 90));
            root.Controls.Add(row3, 0, 3);

            _hint.Text = L10n.T("支持类型：") + string.Join(" / ", PrintFileTypes.All) +
                         L10n.T("  ·  纸张可选 A0–A5（A0/A1 自动换算毫米尺寸，需打印机驱动支持大幅面）");
            _hint.ForeColor = Color.DimGray;
            _hint.AutoSize = true;
            root.Controls.Add(_hint, 0, 4);

            _output.ReadOnly = true;
            _output.Dock = DockStyle.Fill;
            _output.BackColor = Color.FromArgb(248, 250, 252);
            root.Controls.Add(_output, 0, 5);

            Controls.Add(root);
        }

        public void RefreshData()
        {
            // 构造函数里首次调用时句柄尚未创建，直接跳过打印机的 UI 刷新（届时列表为空也无意义）；
            // 由 ShowMain 在窗口显示后再次调用完成实际加载，避免访问未创建句柄的控件。
            if (!IsHandleCreated) return;
            RefreshPrinters();
        }

        private void RefreshPrinters()
        {
            var current = _printers.SelectedItem as string;
            _printers.Items.Clear();
            var result = JObject.Parse(CommandDispatcher.Execute("GetPrinterList", "{}"));
            var names = result["data"]?["printers"] as JArray;
            if (names != null) foreach (var n in names) _printers.Items.Add(n.ToString());
            var primary = result["data"]?["primary"]?.ToString();
            if (!string.IsNullOrWhiteSpace(primary) && _printers.Items.Contains(primary))
                _printers.SelectedItem = primary;
            else if (!string.IsNullOrWhiteSpace(current) && _printers.Items.Contains(current))
                _printers.SelectedItem = current;
            else if (_printers.Items.Count > 0) _printers.SelectedIndex = 0;
        }

        private void Browse()
        {
            using (var dlg = new OpenFileDialog { Filter = PrintFileTypes.Filter, CheckFileExists = true })
            {
                if (dlg.ShowDialog() == DialogResult.OK) _path.Text = dlg.FileName;
            }
        }

        private void TestPage()
        {
            var payload = new JObject { ["printer"] = _printers.SelectedItem as string ?? "" }.ToString();
            Append("PrintTestPage", CommandDispatcher.Execute("PrintTestPage", payload));
        }

        private void Run(string cmd)
        {
            if (string.IsNullOrWhiteSpace(_path.Text))
            {
                MessageBox.Show(L10n.T("请先选择文件或填写 URL。"), L10n.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var payload = new JObject
            {
                ["path"] = _path.Text.Trim(),
                ["printer"] = _printers.SelectedItem as string ?? "",
                ["copies"] = (int)_copies.Value,
                ["fit"] = _fit.SelectedItem as string ?? "fit",
                ["scale"] = _scale.SelectedItem as string ?? "portrait",
                ["paperKind"] = _paper.Text.Trim(),
                ["printPages"] = string.IsNullOrWhiteSpace(_pages.Text) ? "1-" : _pages.Text.Trim(),
                ["duplex"] = DuplexFromCombo(_duplex.SelectedItem as string),
                ["margin"] = (int)_margin.Value,
                ["pageOrder"] = PageOrderFromCombo(_pageOrder.SelectedItem as string)
            };
            Append(cmd, CommandDispatcher.Execute(cmd, payload.ToString()));
        }

        // 组合框存的就是协议值，这里做容错归一；未选则回落 AppSettings 默认值，由 BuildJob 再兜底。
        private static string DuplexFromCombo(string v)
        {
            var s = (v ?? "").Trim().ToLowerInvariant();
            return s == "duplexlong" || s == "duplexshort" || s == "duplex" || s == "simplex" ? s : "simplex";
        }

        private static string PageOrderFromCombo(string v)
        {
            var s = (v ?? "").Trim().ToLowerInvariant();
            return s == "fwd" || s == "rev" || s == "fwdcoll" || s == "revcoll" ? s : "";
        }

        private void Append(string cmd, string json)
        {
            _output.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + cmd + Environment.NewLine);
            try { _output.AppendText(JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.Indented)); }
            catch { _output.AppendText(json); }
            _output.AppendText(Environment.NewLine + new string('-', 70) + Environment.NewLine);
        }
    }
}

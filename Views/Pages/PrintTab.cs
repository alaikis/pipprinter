using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DuoAssistant.Services;
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
        private readonly TextBox _paper = Ui.Input(90);
        private readonly TextBox _pages = Ui.Input(90);
        private readonly TextBox _output = Ui.Input(0, true);
        private readonly Label _hint = Ui.Label("");

        public PrintTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
            BuildUi();
            RefreshData();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var row1 = Ui.Toolbar(
                Ui.Label("文件 / URL："), _path,
                Ui.Button("浏览…", (s, e) => Browse(), 80),
                Ui.Label("打印机："), _printers,
                Ui.Button("刷新", (s, e) => RefreshPrinters(), 70));
            root.Controls.Add(row1, 0, 0);

            _fit.Items.AddRange(new object[] { "fit", "shrink", "noscale", "compat" });
            _fit.SelectedIndex = 0;
            _scale.Items.AddRange(new object[] { "portrait", "landscape" });
            _scale.SelectedIndex = 0;
            _pages.Text = "1-";
            var row2 = Ui.Toolbar(
                Ui.Label("份数："), _copies,
                Ui.Label("缩放："), _fit,
                Ui.Label("方向："), _scale,
                Ui.Label("纸张："), _paper,
                Ui.Label("页码："), _pages);
            root.Controls.Add(row2, 0, 1);

            var row3 = Ui.Toolbar(
                Ui.Button("立即打印", (s, e) => Run("PrintFile"), 120),
                Ui.Button("加入队列", (s, e) => Run("QueuePrint"), 120),
                Ui.Button("打印测试页", (s, e) => TestPage(), 120),
                Ui.Button("清空输出", (s, e) => _output.Clear(), 90));
            root.Controls.Add(row3, 0, 2);

            _hint.Text = "支持类型：" + string.Join(" / ", PrintFileTypes.All) +
                         "  ·  PDF 走 SumatraPDF，Office/图片/文本走系统关联程序，HTML 走无头浏览器转 PDF";
            _hint.ForeColor = Color.DimGray;
            _hint.AutoSize = true;
            root.Controls.Add(_hint, 0, 3);

            _output.ReadOnly = true;
            _output.Dock = DockStyle.Fill;
            _output.BackColor = Color.FromArgb(248, 250, 252);
            root.Controls.Add(_output, 0, 4);

            Controls.Add(root);
        }

        public void RefreshData() => RefreshPrinters();

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
                MessageBox.Show("请先选择文件或填写 URL。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                ["printPages"] = string.IsNullOrWhiteSpace(_pages.Text) ? "1-" : _pages.Text.Trim()
            };
            Append(cmd, CommandDispatcher.Execute(cmd, payload.ToString()));
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

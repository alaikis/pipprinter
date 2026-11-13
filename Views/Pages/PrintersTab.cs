using System;
using System.Drawing;
using System.Windows.Forms;
using DuoAssistant.Services;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「打印机」页：本机打印机列表、驱动信息、默认切换、测试页。</summary>
    public class PrintersTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List("打印机名称", "驱动", "端口", "默认", "状态", "纸张规格");
        private readonly Label _info = Ui.Label("", true);

        public PrintersTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
            _list.Columns[0].Width = 240;
            _list.Columns[1].Width = 200;
            _list.Columns[2].Width = 130;
            _list.Columns[3].Width = 70;
            _list.Columns[4].Width = 90;
            _list.Columns[5].Width = 260;

            var bar = Ui.Toolbar(
                Ui.Button("刷新列表", (s, e) => RefreshData(), 100),
                Ui.Button("设为默认", (s, e) => SetDefault(), 100),
                Ui.Button("打印测试页", (s, e) => TestPage(), 110),
                Ui.Button("打开文档", (s, e) => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Program.DocsUrl) { UseShellExecute = true }), 100),
                _info);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            root.Controls.Add(_list);
            root.Controls.Add(bar);
            Controls.Add(root);
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
                    item.SubItems.Add(d["IsDefault"]?.Value<bool>() == true ? "★ 是" : "");
                    item.SubItems.Add(d["Status"]?.ToString() ?? "");
                    item.SubItems.Add(d["PaperSizes"]?.ToString() ?? "");
                    if (d["IsDefault"]?.Value<bool>() == true) item.ForeColor = Color.FromArgb(29, 78, 216);
                    _list.Items.Add(item);
                }
            }
            _list.EndUpdate();
            _info.Text = "本机共 " + _list.Items.Count + " 台打印机 · 当前默认：" +
                         (data?["primary"]?.ToString() ?? "(无)");
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
                MessageBox.Show(res["error"]?.ToString(), "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshData();
        }

        private void TestPage()
        {
            var name = SelectedName();
            var res = JObject.Parse(CommandDispatcher.Execute("PrintTestPage",
                new JObject { ["printer"] = name ?? "" }.ToString()));
            MessageBox.Show(res["error"]?.ToString() ?? "已发送测试页", "测试页",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshData();
        }
    }
}

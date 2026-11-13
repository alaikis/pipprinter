using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Services;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>
    /// 「磁盘」页：扫描临时/大文件、试运行、确认清理。
    /// 默认只扫描应用临时目录与系统 Temp；删除默认走回收站且需二次确认。
    /// </summary>
    public class DiskTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List("大小", "最后修改", "类别", "路径");
        private readonly Label _summary = Ui.Label("", true);
        private readonly Label _drives = Ui.Label("");
        private readonly CheckBox _recycle = new CheckBox
        {
            Text = "删除时移入回收站", Checked = true, AutoSize = true,
            Font = Ui.LabelFont, Padding = new Padding(0, 8, 8, 0)
        };

        public DiskTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
            _list.CheckBoxes = true;
            _list.Columns[0].Width = 100;
            _list.Columns[1].Width = 140;
            _list.Columns[2].Width = 100;
            _list.Columns[3].Width = 620;

            var bar = Ui.Toolbar(
                Ui.Button("扫描", (s, e) => RefreshData(), 80),
                Ui.Button("全选", (s, e) => CheckAll(true), 80),
                Ui.Button("全不选", (s, e) => CheckAll(false), 90),
                Ui.Button("试运行", (s, e) => Clean(true), 90),
                Ui.Button("确认清理", (s, e) => Clean(false), 100),
                Ui.Button("导出清单", (s, e) => Export(), 100),
                _recycle,
                _summary);

            var drivesBar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 6, 0, 0) };
            drivesBar.Controls.Add(_drives);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            root.Controls.Add(_list);
            root.Controls.Add(drivesBar);
            root.Controls.Add(bar);
            Controls.Add(root);
            RefreshData();
        }

        public void RefreshData()
        {
            var res = JObject.Parse(CommandDispatcher.Execute("ScanDisk", "{}"));
            var data = res["data"] as JObject;
            var items = data?["items"] as JArray ?? new JArray();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var it in items)
            {
                var item = new ListViewItem(it["SizeText"]?.ToString() ?? "");
                item.SubItems.Add(it["LastWrite"]?.Value<DateTime>().ToString("yyyy-MM-dd HH:mm") ?? "");
                item.SubItems.Add(it["Category"]?.ToString() ?? "");
                item.SubItems.Add(it["Path"]?.ToString() ?? "");
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _summary.Text = string.Format("发现 {0} 项 · 合计 {1}{2}",
                data?["count"] ?? 0, data?["totalText"] ?? "0 B",
                data?["truncated"]?.Value<bool>() == true ? "（仅显示前 500 项）" : "");

            var info = JObject.Parse(CommandDispatcher.Execute("GetDiskInfo", "{}"))["data"] as JObject;
            var drives = info?["drives"] as JArray ?? new JArray();
            _drives.Text = "磁盘：" + string.Join("   ", drives.Select(d =>
                d["name"] + " " + d["usedPercent"] + "% 已用 / 可用 " + d["free"]));
        }

        private void CheckAll(bool value)
        {
            foreach (ListViewItem item in _list.Items) item.Checked = value;
        }

        private List<string> SelectedPaths()
        {
            return _list.Items.Cast<ListViewItem>()
                .Where(x => x.Checked).Select(x => x.SubItems[3].Text).ToList();
        }

        private void Clean(bool dryRun)
        {
            var paths = SelectedPaths();
            if (paths.Count == 0)
            {
                MessageBox.Show("请先勾选要处理的文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!dryRun &&
                MessageBox.Show(
                    "⚠️ 即将删除 " + paths.Count + " 个文件。\n\n" +
                    "仅会删除应用临时目录与系统 Temp 下的文件，且需通过白名单校验。\n" +
                    (_recycle.Checked ? "文件将移入回收站，可恢复。" : "文件将被永久删除，不可恢复！") +
                    "\n\n确认继续？",
                    "清理确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var payload = new JObject
            {
                ["dryRun"] = dryRun,
                ["useRecycleBin"] = _recycle.Checked,
                ["paths"] = JArray.FromObject(paths)
            };
            var res = JObject.Parse(CommandDispatcher.Execute("CleanDisk", payload.ToString()));
            var d = res["data"] as JObject;
            var msg = string.Format("请求 {0} 项 · 处理 {1} 项 · 跳过 {2} 项 · 释放 {3}\n{4}",
                d?["Requested"], d?["Deleted"], d?["Skipped"], d?["FreedText"], res["error"]);
            if (d?["Errors"] is JArray errs && errs.Count > 0)
                msg += "\n错误：" + string.Join("; ", errs.Select(x => x.ToString()));
            MessageBox.Show(msg, dryRun ? "试运行结果" : "清理结果",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!dryRun) RefreshData();
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = "clean-list.txt" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                File.WriteAllLines(dlg.FileName, SelectedPaths());
                MessageBox.Show("清单已导出。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}

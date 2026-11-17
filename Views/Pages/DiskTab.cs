// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>
    /// 「磁盘」页：扫描临时/大文件、试运行、确认清理。
    /// 默认只扫描应用临时目录与系统 Temp；删除默认走回收站且需二次确认。
    /// </summary>
    public class DiskTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List(
            L10n.T("大小"), L10n.T("最后修改"), L10n.T("类别"), L10n.T("路径"));
        private readonly Label _summary = Ui.Label("", true);
        private readonly Label _drives = Ui.Label("");
        private readonly CheckBox _recycle = new CheckBox
        {
            Text = "", Checked = true, AutoSize = true,
            Font = Ui.LabelFont, Padding = new Padding(0, 8, 8, 0)
        };

        public DiskTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;
            _recycle.Text = L10n.T("删除时移入回收站");
            _list.CheckBoxes = true;
            _list.Columns[0].Width = 100;
            _list.Columns[1].Width = 140;
            _list.Columns[2].Width = 100;
            _list.Columns[3].Width = 620;

            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("扫描"), (s, e) => RefreshData(), 80),
                Ui.Button(L10n.T("全选"), (s, e) => CheckAll(true), 80),
                Ui.Button(L10n.T("全不选"), (s, e) => CheckAll(false), 90),
                Ui.Button(L10n.T("试运行"), (s, e) => Clean(true), 90),
                Ui.Button(L10n.T("确认清理"), (s, e) => Clean(false), 100, primary: true),
                Ui.Button(L10n.T("清理应用临时文件"), (s, e) => CleanAppTemp(), 150),
                Ui.Button(L10n.T("导出清单"), (s, e) => Export(), 100),
                _recycle,
                _summary);

            var drivesBar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 6, 0, 0) };
            drivesBar.Controls.Add(_drives);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
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
            _summary.Text = L10n.F("发现 {0} 项 · 合计 {1}{2}",
                data?["count"] ?? 0, data?["totalText"] ?? "0 B",
                data?["truncated"]?.Value<bool>() == true ? L10n.T("（仅显示前 500 项）") : "");

            var info = JObject.Parse(CommandDispatcher.Execute("GetDiskInfo", "{}"))["data"] as JObject;
            var drives = info?["drives"] as JArray ?? new JArray();
            _drives.Text = L10n.T("磁盘：") + string.Join("   ", drives.Select(d =>
                d["name"] + " " + d["usedPercent"] + L10n.T("% 已用 / 可用 ") + d["free"]));
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
                MessageBox.Show(L10n.T("请先勾选要处理的文件。"), L10n.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!dryRun &&
                MessageBox.Show(
                    string.Format(L10n.T("⚠️ 即将删除 {0} 个文件。\n\n仅会删除应用临时目录与系统 Temp 下的文件，且需通过白名单校验。\n{1}\n\n确认继续？"),
                        paths.Count,
                        _recycle.Checked ? L10n.T("文件将移入回收站，可恢复。") : L10n.T("文件将被永久删除，不可恢复！")),
                    L10n.T("清理确认"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var payload = new JObject
            {
                ["dryRun"] = dryRun,
                ["useRecycleBin"] = _recycle.Checked,
                ["paths"] = JArray.FromObject(paths)
            };
            var res = JObject.Parse(CommandDispatcher.Execute("CleanDisk", payload.ToString()));
            var d = res["data"] as JObject;
            var msg = L10n.F("请求 {0} 项 · 处理 {1} 项 · 跳过 {2} 项 · 释放 {3}\n{4}",
                d?["Requested"], d?["Deleted"], d?["Skipped"], d?["FreedText"], res["error"]);
            if (d?["Errors"] is JArray errs && errs.Count > 0)
                msg += "\n" + L10n.T("错误：") + string.Join("; ", errs.Select(x => x.ToString()));
            MessageBox.Show(msg, dryRun ? L10n.T("试运行结果") : L10n.T("清理结果"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!dryRun) RefreshData();
        }

        /// <summary>
        /// 一键清理应用自产临时文件（条码 PNG、ESC/POS dump、转换中间件等）。
        /// 先试运行列出数量，确认后走回收站删除（可恢复）。
        /// </summary>
        private void CleanAppTemp()
        {
            // 1) 试运行：拿数量与体积
            var probe = JObject.Parse(CommandDispatcher.Execute("CleanAppTemp",
                "{\"dryRun\":true,\"maxAgeDays\":0,\"minSizeMB\":0}"));
            var pd = probe["data"] as JObject;
            var count = pd?["count"]?.Value<int>() ?? 0;
            if (count == 0)
            {
                MessageBox.Show(L10n.T("应用临时目录没有可清理的文件。"), L10n.T("提示"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var totalText = pd?["totalText"]?.ToString() ?? "0 B";
            if (MessageBox.Show(
                    string.Format(L10n.T("发现 {0} 个应用临时文件，合计 {1}。\n\n删除后将移入回收站（可恢复）。确认继续？"),
                        count, totalText),
                    L10n.T("清理应用临时文件"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            // 2) 真正删除（回收站）
            var res = JObject.Parse(CommandDispatcher.Execute("CleanAppTemp",
                "{\"dryRun\":false,\"maxAgeDays\":0,\"minSizeMB\":0,\"useRecycleBin\":true}"));
            var d = res["data"] as JObject;
            var msg = L10n.F("已清理 {0} 个 · 跳过 {1} 个 · 释放 {2}",
                d?["deleted"] ?? 0, d?["skipped"] ?? 0, d?["freedText"] ?? "0 B");
            if (d?["errors"] is JArray errs && errs.Count > 0)
                msg += "\n" + L10n.T("错误：") + string.Join("; ", errs.Select(x => x.ToString()));
            MessageBox.Show(msg, L10n.T("清理结果"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshData();
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog { Filter = L10n.T("文本文件|*.txt"), FileName = "clean-list.txt" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                File.WriteAllLines(dlg.FileName, SelectedPaths());
                MessageBox.Show(L10n.T("清单已导出。"), L10n.T("完成"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}

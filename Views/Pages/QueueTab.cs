// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Models;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「打印队列」页：顺序任务、状态、失败重试。</summary>
    public class QueueTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List(
            L10n.T("任务 ID"), L10n.T("文件"), L10n.T("打印机"), L10n.T("份数"),
            L10n.T("状态"), L10n.T("入队时间"), L10n.T("完成时间"), L10n.T("错误"));
        private readonly Label _summary = Ui.Label("", true);
        private readonly Timer _timer = new Timer { Interval = 2000 };

        public QueueTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;
            _list.CheckBoxes = false;
            _list.Columns[0].Width = 210;
            _list.Columns[1].Width = 200;
            _list.Columns[2].Width = 160;
            _list.Columns[3].Width = 60;
            _list.Columns[4].Width = 90;
            _list.Columns[5].Width = 140;
            _list.Columns[6].Width = 140;
            _list.Columns[7].Width = 260;

            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新"), (s, e) => RefreshData(), 80),
                Ui.Button(L10n.T("重试选中"), (s, e) => Act("RetryJob"), 100, primary: true),
                Ui.Button(L10n.T("取消选中"), (s, e) => Act("CancelJob"), 100),
                Ui.Button(L10n.T("删除选中"), (s, e) => RemoveSelected(), 100),
                Ui.Button(L10n.T("清空已完成"), (s, e) => Clear(true), 110),
                Ui.Button(L10n.T("清空全部"), (s, e) => Clear(false), 100),
                _summary);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            root.Controls.Add(_list);
            root.Controls.Add(bar);
            Controls.Add(root);

            PrintQueueService.Instance.Changed += (s, e) => Ui.Safe(this, RefreshData);
            _timer.Tick += (s, e) => RefreshData();
            _timer.Start();
            RefreshData();

            // 控件销毁时停止并释放定时器，避免页签切换/窗口关闭后仍后台刷新导致对象泄漏。
            Disposed += (s, e) => { _timer.Stop(); _timer.Dispose(); };
        }

        public void RefreshData()
        {
            if (!IsHandleCreated && !IsDisposed) { /* 首次由构造函数调用，直接绘制 */ }
            var selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Text : null;
            var jobs = PrintQueueService.Instance.GetAll();
            var sum = JObject.FromObject(PrintQueueService.Instance.Summary());

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var j in jobs)
            {
                var item = new ListViewItem(j.Id);
                item.SubItems.Add(j.FileName);
                item.SubItems.Add(string.IsNullOrWhiteSpace(j.Printer) ? L10n.T("(默认)") : j.Printer);
                item.SubItems.Add(j.Copies.ToString());
                item.SubItems.Add(StatusText(j.Status));
                item.SubItems.Add(j.EnqueuedAt.ToString("MM-dd HH:mm:ss"));
                item.SubItems.Add(j.FinishedAt?.ToString("MM-dd HH:mm:ss") ?? "-");
                item.SubItems.Add(j.Error ?? "");
                item.Tag = j;
                item.ForeColor = StatusColor(j.Status);
                _list.Items.Add(item);
                if (selected == j.Id) item.Selected = true;
            }
            _list.EndUpdate();

            _summary.Text = L10n.F("共 {0} · 等待 {1} · 打印中 {2} · 完成 {3} · 失败 {4}",
                sum["total"], sum["pending"], sum["printing"], sum["done"], sum["failed"]);
        }

        private static string StatusText(string s)
        {
            switch (s)
            {
                case JobStatus.Pending: return L10n.T("等待中");
                case JobStatus.Printing: return L10n.T("打印中");
                case JobStatus.Done: return L10n.T("已完成");
                case JobStatus.Failed: return L10n.T("失败");
                case JobStatus.Cancelled: return L10n.T("已取消");
                default: return s;
            }
        }

        private static Color StatusColor(string s)
        {
            switch (s)
            {
                case JobStatus.Done: return Color.FromArgb(22, 101, 52);
                case JobStatus.Failed: return Color.FromArgb(185, 28, 28);
                case JobStatus.Printing: return Color.FromArgb(29, 78, 216);
                case JobStatus.Cancelled: return Color.Gray;
                default: return Theme.Text;
            }
        }

        private string SelectedId()
        {
            return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Text : null;
        }

        private void Act(string cmd)
        {
            var id = SelectedId();
            if (id == null) return;
            CommandDispatcher.Execute(cmd, new JObject { ["id"] = id }.ToString());
            RefreshData();
        }

        private void RemoveSelected()
        {
            var ids = _list.SelectedItems.Cast<ListViewItem>().Select(x => x.Text).ToList();
            foreach (var id in ids)
            {
                // Remove 未暴露为命令（避免网络侧滥用），直接调用队列服务
                PrintQueueService.Instance.Remove(id);
            }
            RefreshData();
        }

        private void Clear(bool finishedOnly)
        {
            if (!finishedOnly &&
                MessageBox.Show(L10n.T("确认清空全部队列任务？正在打印的任务会保留。"), L10n.T("确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            CommandDispatcher.Execute("ClearQueue",
                new JObject { ["finishedOnly"] = finishedOnly }.ToString());
            RefreshData();
        }
    }
}

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Models;
using DuoAssistant.Services;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「打印队列」页：顺序任务、状态、失败重试。</summary>
    public class QueueTab : UserControl, IRefreshable
    {
        private readonly ListView _list = Ui.List("任务 ID", "文件", "打印机", "份数", "状态", "入队时间", "完成时间", "错误");
        private readonly Label _summary = Ui.Label("", true);
        private readonly Timer _timer = new Timer { Interval = 2000 };

        public QueueTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
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
                Ui.Button("刷新", (s, e) => RefreshData(), 80),
                Ui.Button("重试选中", (s, e) => Act("RetryJob"), 100),
                Ui.Button("取消选中", (s, e) => Act("CancelJob"), 100),
                Ui.Button("删除选中", (s, e) => RemoveSelected(), 100),
                Ui.Button("清空已完成", (s, e) => Clear(true), 110),
                Ui.Button("清空全部", (s, e) => Clear(false), 100),
                _summary);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            root.Controls.Add(_list);
            root.Controls.Add(bar);
            Controls.Add(root);

            PrintQueueService.Instance.Changed += (s, e) => Ui.Safe(this, RefreshData);
            _timer.Tick += (s, e) => RefreshData();
            _timer.Start();
            RefreshData();
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
                item.SubItems.Add(string.IsNullOrWhiteSpace(j.Printer) ? "(默认)" : j.Printer);
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

            _summary.Text = string.Format("共 {0} · 等待 {1} · 打印中 {2} · 完成 {3} · 失败 {4}",
                sum["total"], sum["pending"], sum["printing"], sum["done"], sum["failed"]);
        }

        private static string StatusText(string s)
        {
            switch (s)
            {
                case JobStatus.Pending: return "等待中";
                case JobStatus.Printing: return "打印中";
                case JobStatus.Done: return "已完成";
                case JobStatus.Failed: return "失败";
                case JobStatus.Cancelled: return "已取消";
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
                default: return SystemColors.WindowText;
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
                MessageBox.Show("确认清空全部队列任务？正在打印的任务会保留。", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            CommandDispatcher.Execute("ClearQueue",
                new JObject { ["finishedOnly"] = finishedOnly }.ToString());
            RefreshData();
        }
    }
}

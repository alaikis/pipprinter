using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Services;

namespace DuoAssistant.Views.Pages
{
    /// <summary>「日志」页：只读查看 log4net 按级别落盘的日志尾部。</summary>
    public class LogsTab : UserControl, IRefreshable
    {
        private readonly ComboBox _category = Ui.Combo(140);
        private readonly TextBox _viewer = Ui.Input(0, true);
        private readonly CheckBox _auto = new CheckBox
        {
            Text = "自动刷新", Checked = true, AutoSize = true, Font = Ui.LabelFont,
            Padding = new Padding(0, 8, 8, 0)
        };
        private readonly Timer _timer = new Timer { Interval = 3000 };
        private readonly Label _status = Ui.Label("");

        public LogsTab()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Window;
            _category.Items.AddRange(new object[] { "debug", "info", "warn", "err", "fatal" });
            _category.SelectedIndex = 0;
            _category.SelectedIndexChanged += (s, e) => RefreshData();

            _viewer.ReadOnly = true;
            _viewer.Dock = DockStyle.Fill;
            _viewer.BackColor = SystemColors.Window;
            _viewer.Font = new System.Drawing.Font("Consolas", 9F);

            var bar = Ui.Toolbar(
                Ui.Label("日志级别："), _category,
                Ui.Button("刷新", (s, e) => RefreshData(), 80),
                Ui.Button("打开日志目录", (s, e) => OpenDir(), 130),
                _auto, _status);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            root.Controls.Add(_viewer);
            root.Controls.Add(bar);
            Controls.Add(root);

            _timer.Tick += (s, e) => { if (_auto.Checked) RefreshData(); };
            _timer.Start();
            RefreshData();
        }

        public void RefreshData()
        {
            try
            {
                var dir = SettingsStore.Instance.LogDir;
                if (!Directory.Exists(dir)) { _status.Text = "日志目录尚未生成"; return; }
                var category = _category.SelectedItem?.ToString() ?? "debug";
                var file = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories)
                    .Where(f => f.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (file == null) { _viewer.Text = "暂无 " + category + " 级别日志。"; _status.Text = ""; return; }

                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();
                // 只保留尾部 200KB，避免 UI 卡顿
                if (text.Length > 200_000) text = text.Substring(text.Length - 200_000);
                _viewer.Text = text;
                _viewer.SelectionStart = _viewer.TextLength;
                _viewer.ScrollToCaret();
                _status.Text = Path.GetFileName(file) + " · " + new FileInfo(file).Length + " bytes";
            }
            catch (Exception ex) { _status.Text = "读取失败: " + ex.Message; }
        }

        private void OpenDir()
        {
            var dir = SettingsStore.Instance.LogDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }
}

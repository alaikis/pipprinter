using System;
using System.Drawing;
using System.Windows.Forms;

namespace DuoAssistant.Views.Pages
{
    /// <summary>页面可刷新的统一接口，主窗体切换页签时调用。</summary>
    public interface IRefreshable
    {
        void RefreshData();
    }

    /// <summary>轻量 UI 构建辅助，统一控件样式与跨线程封送。</summary>
    public static class Ui
    {
        public static readonly Font LabelFont = new Font("Microsoft YaHei", 9F);
        public static readonly Font BoldFont = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);

        public static Button Button(string text, EventHandler onClick, int width = 110)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                Font = LabelFont,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(4, 3, 4, 3)
            };
        }

        public static Label Label(string text, bool bold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = bold ? BoldFont : LabelFont,
                Padding = new Padding(0, 6, 8, 0)
            };
        }

        public static TextBox Input(int width = 320, bool multiline = false)
        {
            var t = new TextBox { Width = width, Font = LabelFont };
            if (multiline)
            {
                t.Multiline = true;
                t.ScrollBars = ScrollBars.Both;
                t.WordWrap = false;
            }
            return t;
        }

        public static ComboBox Combo(int width = 180)
        {
            return new ComboBox { Width = width, Font = LabelFont, DropDownStyle = ComboBoxStyle.DropDownList };
        }

        public static NumericUpDown Number(int min, int max, int value, int width = 80)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = value,
                Width = width, Font = LabelFont
            };
        }

        /// <summary>跨线程安全调用：控件句柄未创建或已释放时静默跳过。</summary>
        public static void Safe(Control c, Action action)
        {
            if (c == null || c.IsDisposed || action == null) return;
            try
            {
                if (c.InvokeRequired) c.BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        public static FlowLayoutPanel Toolbar(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(6),
                Font = LabelFont,
                WrapContents = true
            };
            foreach (var c in controls) p.Controls.Add(c);
            return p;
        }

        public static ListView List(params string[] columns)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = LabelFont,
                HideSelection = false,
                MultiSelect = true
            };
            foreach (var c in columns) lv.Columns.Add(c, 120);
            return lv;
        }

        public static void AutoColumns(ListView lv)
        {
            try { lv.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent); }
            catch { /* 忽略 */ }
        }
    }
}

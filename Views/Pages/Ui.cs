// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DuoAssistant.Utils;

namespace DuoAssistant.Views.Pages
{
    /// <summary>页面可刷新的统一接口，主窗体切换页签时调用。</summary>
    public interface IRefreshable
    {
        void RefreshData();
    }

    /// <summary>轻量 UI 构建辅助，统一控件样式与跨线程封送。颜色取自 Theme。</summary>
    public static class Ui
    {
        /// <summary>自绘 ListView 默认不带双缓冲，定时重建行时整表闪动；这里统一开启。</summary>
        private sealed class BufferedListView : ListView
        {
            public BufferedListView()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.ResizeRedraw, true);
            }
        }

        public static readonly Font LabelFont = Theme.BodyFont;

        public static Button Button(string text, EventHandler onClick, int width = 110, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                Font = LabelFont,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(4, 3, 4, 3),
                Cursor = Cursors.Hand
            };
            if (primary)
            {
                b.BackColor = Theme.Primary;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = Theme.PrimaryDark;
                b.FlatAppearance.MouseOverBackColor = Theme.PrimaryHover;
                b.FlatAppearance.MouseDownBackColor = Theme.PrimaryDark;
            }
            else
            {
                b.BackColor = Theme.Card;
                b.ForeColor = Theme.Text;
                b.FlatAppearance.BorderColor = Theme.Border;
                b.FlatAppearance.MouseOverBackColor = Theme.PrimaryFaint;
                b.FlatAppearance.MouseDownBackColor = Theme.PrimarySoft;
            }
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Label Label(string text, bool bold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = bold ? Theme.TitleFont : LabelFont,
                ForeColor = Theme.Text,
                Padding = new Padding(0, 6, 8, 0)
            };
        }

        public static TextBox Input(int width = 320, bool multiline = false)
        {
            var t = new TextBox
            {
                Width = width,
                Font = LabelFont,
                BackColor = Theme.Card,
                ForeColor = Theme.Text
            };
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
            return new ComboBox
            {
                Width = width, Font = LabelFont,
                BackColor = Theme.Card, ForeColor = Theme.Text,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
        }

        /// <summary>可编辑下拉：预设可选，也允许直接键入自定义值。</summary>
        public static ComboBox EditableCombo(int width = 180)
        {
            return new ComboBox
            {
                Width = width, Font = LabelFont,
                BackColor = Theme.Card, ForeColor = Theme.Text,
                DropDownStyle = ComboBoxStyle.DropDown
            };
        }

        public static NumericUpDown Number(int min, int max, int value, int width = 80)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = value,
                Width = width, Font = LabelFont,
                BackColor = Theme.Card, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle
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
                BackColor = Theme.Card,
                WrapContents = true
            };
            foreach (var c in controls) p.Controls.Add(c);
            return p;
        }

        /// <summary>主题化 ListView：无网格线、圆角表头、浅红行选中。</summary>
        public static ListView List(params string[] columns)
        {
            var lv = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                Font = LabelFont,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                HideSelection = false,
                MultiSelect = true,
                OwnerDraw = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            foreach (var c in columns) lv.Columns.Add(c, 120);

            lv.DrawColumnHeader += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(ColorTranslator.FromHtml("#F7F8FA")))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                using (var pen = new Pen(Theme.BorderLight))
                    e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                using (var font = new Font("Microsoft YaHei", 9F, FontStyle.Regular))
                using (var brush = new SolidBrush(Theme.Muted))
                {
                    var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
                    var rect = e.Bounds; rect.Inflate(-8, 0);
                    e.Graphics.DrawString(e.Header.Text, font, brush, rect, sf);
                }
            };

            lv.DrawItem += (s, e) =>
            {
                var bounds = e.Bounds;
                var selected = (e.State & ListViewItemStates.Selected) != 0;
                var hot = false;
                try { hot = e.Item?.Bounds.Contains(lv.PointToClient(Cursor.Position)) == true; } catch { }

                Color bg;
                if (selected) bg = Theme.PrimarySoft;
                else if (hot) bg = Theme.PrimaryFaint;
                else bg = Theme.Card;

                using (var brush = new SolidBrush(bg))
                    e.Graphics.FillRectangle(brush, bounds);
                using (var pen = new Pen(Theme.BorderLight))
                    e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

                // 选中行左侧强调条
                if (selected)
                using (var brush = new SolidBrush(Theme.Primary))
                    e.Graphics.FillRectangle(brush, bounds.X, bounds.Y + 2, 3, bounds.Height - 4);
            };

            lv.DrawSubItem += (s, e) =>
            {
                var selected = (e.ItemState & ListViewItemStates.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Theme.PrimaryDark : Theme.Text))
                {
                    var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
                    var rect = e.Bounds; rect.Inflate(-8, 0);
                    e.Graphics.DrawString(e.SubItem.Text, lv.Font, brush, rect, sf);
                }
            };

            return lv;
        }

        public static void AutoColumns(ListView lv)
        {
            try { lv.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent); }
            catch { /* 忽略 */ }
        }
    }
}

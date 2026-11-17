// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 全局主题：荔枝红为主色，Wise 风格设计语言
    /// （浅灰底 + 白色卡片 + 圆角 + 充足留白 + 单一强调色 + 柔和层级）。
    /// 所有页面控件颜色统一从这里取，避免散落硬编码。
    /// </summary>
    public static class Theme
    {
        // ---- 荔枝红主色系 ----
        public static readonly Color Primary = ColorTranslator.FromHtml("#E8455A");        // 荔枝红
        public static readonly Color PrimaryDark = ColorTranslator.FromHtml("#C93347");    // 按压/描边
        public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#D83A4E");   // 悬停
        public static readonly Color PrimarySoft = ColorTranslator.FromHtml("#FDECEF");    // 选中/浅底
        public static readonly Color PrimaryFaint = ColorTranslator.FromHtml("#FFF5F6");   // 行悬停

        // ---- 中性色 ----
        public static readonly Color Bg = ColorTranslator.FromHtml("#F5F6F7");             // 窗体底
        public static readonly Color Card = Color.White;                                   // 卡片/内容面
        public static readonly Color Border = ColorTranslator.FromHtml("#E4E7EB");         // 描边
        public static readonly Color BorderLight = ColorTranslator.FromHtml("#EEF0F2");    // 行分隔线
        public static readonly Color Text = ColorTranslator.FromHtml("#1F2937");           // 主文字
        public static readonly Color Muted = ColorTranslator.FromHtml("#6B7280");          // 次文字

        // ---- 语义色 ----
        public static readonly Color Success = ColorTranslator.FromHtml("#2E9E5B");        // 荔枝叶绿
        public static readonly Color Danger = ColorTranslator.FromHtml("#E0243A");

        // ---- 字体 ----
        public static readonly Font TitleFont = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
        public static readonly Font BodyFont = new Font("Microsoft YaHei", 9F);
        public static readonly Font MonoFont = new Font("Consolas", 9F);

        // ---- 圆角 ----
        public const int Radius = 8;

        /// <summary>构造圆角矩形路径。</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 递归应用主题。在控件树构建完成后调用一次。
        /// </summary>
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                ApplyToControl(c);
                if (c.Controls.Count > 0) Apply(c);
            }
        }

        private static void ApplyToControl(Control c)
        {
            // 已是主题控件（按钮/列表/页签等）不覆盖其专用配色
            if (c is Button || c is ListView || c is TabControl) return;

            if (c is Form f) { f.BackColor = Bg; return; }
            if (c is UserControl uc) { uc.BackColor = Card; return; }
            if (c is TextBox tb) { tb.BackColor = Card; tb.ForeColor = Text; return; }
            if (c is ComboBox cb) { cb.BackColor = Card; cb.ForeColor = Text; return; }
            if (c is NumericUpDown nu) { nu.BackColor = Card; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle; return; }
            if (c is Label lb) { lb.ForeColor = Text; return; }
            if (c is CheckBox chk) { chk.ForeColor = Text; chk.FlatStyle = FlatStyle.System; return; }
            if (c is Panel pn) { pn.BackColor = Card; return; }
            if (c is StatusStrip ss) { ss.BackColor = Card; ss.ForeColor = Muted; return; }
            if (c is ContextMenuStrip cms) { cms.BackColor = Card; cms.ForeColor = Text; return; }
            if (c is TabPage tp) { tp.BackColor = Card; tp.ForeColor = Text; return; }
        }

        /// <summary>主题化 ToolStrip / StatusStrip / ContextMenuStrip 渲染器。</summary>
        public sealed class ToolStripRenderer : ToolStripProfessionalRenderer
        {
            public ToolStripRenderer() : base(new Palette()) { }

            private sealed class Palette : ProfessionalColorTable
            {
                public override Color ToolStripGradientBegin => Card;
                public override Color ToolStripGradientMiddle => Card;
                public override Color ToolStripGradientEnd => Card;
                public override Color ToolStripBorder => BorderLight;
                public override Color StatusStripGradientBegin => Card;
                public override Color StatusStripGradientEnd => Card;
                public override Color MenuBorder => Border;
                public override Color MenuItemBorder => Primary;
                public override Color MenuItemSelected => PrimarySoft;
                public override Color MenuItemSelectedGradientBegin => PrimarySoft;
                public override Color MenuItemSelectedGradientEnd => PrimarySoft;
                public override Color MenuItemPressedGradientBegin => PrimarySoft;
                public override Color MenuItemPressedGradientEnd => PrimarySoft;
                public override Color SeparatorDark => BorderLight;
                public override Color SeparatorLight => BorderLight;
                public override Color ImageMarginGradientBegin => Card;
                public override Color ImageMarginGradientEnd => Card;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }
        }
    }
}

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 水印叠加：对图片文件在内存渲染半透明斜向文字水印，输出临时 PNG 供打印管线使用。
    /// 不修改原文件；无水印文字时原样返回。PDF 水印由打印管线在页面渲染后叠加（同一入口）。
    /// </summary>
    public static class WatermarkService
    {
        /// <summary>
        /// 给图片加文字水印，返回临时文件路径（调用方打印后自行清理）。
        /// 失败时返回原路径（水印失败不阻断打印）。
        /// </summary>
        public static string ApplyToImage(string filePath, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return filePath;
            try
            {
                using var img = Image.FromFile(filePath);
                var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var fontSize = Math.Max(14, Math.Min(img.Width, img.Height) / 12);
                    using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                    var size = g.MeasureString(text, font);
                    using var brush = new SolidBrush(Color.FromArgb(70, 180, 30, 30));
                    var x = (img.Width - size.Width) / 2;
                    var y = (img.Height - size.Height) / 2;
                    g.TranslateTransform(img.Width / 2f, img.Height / 2f);
                    g.RotateTransform(-30);
                    g.TranslateTransform(-img.Width / 2f, -img.Height / 2f);
                    g.DrawString(text, font, brush, x, y);
                }
                var tmp = Path.Combine(Path.GetTempPath(),
                    "litchio-wm-" + Guid.NewGuid().ToString("N") + ".png");
                bmp.Save(tmp, ImageFormat.Png);
                bmp.Dispose();
                return tmp;
            }
            catch (Exception ex)
            {
                Tools.Logger("水印叠加失败(原样打印): " + ex.Message);
                return filePath;
            }
        }

        /// <summary>是否需要为 PDF 叠加水印（PDF 走打印管线内叠加，此处仅标记）。</summary>
        public static bool NeedsPdfWatermark(string text) => !string.IsNullOrWhiteSpace(text);
    }
}

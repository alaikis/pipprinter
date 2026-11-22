// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>单页预览结果（PNG base64 + 页码）。</summary>
    public class PreviewPage
    {
        public int Page { get; set; }
        public string PngBase64 { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 打印预览：把图片/文本/PDF 渲染为 PNG 缩略图（base64），供 UI 打印前预览与偏移微调。
    /// 图片直接读取缩放；PDF/XPS 走现有渲染组件。渲染失败返回空列表，不阻断打印。
    /// </summary>
    public static class PreviewService
    {
        private const int MaxSide = 900;

        public static List<PreviewPage> Render(string target, int maxPages)
        {
            var list = new List<PreviewPage>();
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target)) return list;
            var ext = Path.GetExtension(target).ToLowerInvariant();
            try
            {
                if (ext == ".pdf" || ext == ".xps" || ext == ".oxps")
                {
                    var pages = new XpsWriterService().RenderPages(target);
                    int n = 0;
                    foreach (var p in pages)
                    {
                        if (n >= maxPages) break;
                        var b64 = Thumbnail(p, out var w, out var h);
                        if (!string.IsNullOrEmpty(b64)) list.Add(new PreviewPage { Page = ++n, PngBase64 = b64, Width = w, Height = h });
                    }
                }
                else if (IsImage(ext))
                {
                    var b64 = Thumbnail(target, out var w, out var h);
                    if (!string.IsNullOrEmpty(b64)) list.Add(new PreviewPage { Page = 1, PngBase64 = b64, Width = w, Height = h });
                }
            }
            catch (Exception ex) { Tools.Logger("预览渲染失败: " + ex.Message); }
            return list;
        }

        private static bool IsImage(string ext) =>
            ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff";

        private static string Thumbnail(string file, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var img = Image.FromFile(file);
                var ratio = Math.Min((double)MaxSide / img.Width, (double)MaxSide / img.Height);
                if (ratio > 1) ratio = 1;
                width = Math.Max(1, (int)(img.Width * ratio));
                height = Math.Max(1, (int)(img.Height * ratio));
                using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, width, height);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex) { Tools.Logger("缩略图生成失败: " + ex.Message); return ""; }
        }
    }
}

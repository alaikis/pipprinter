// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 条码 / 二维码生成器（ZXing.Net）。
    /// 覆盖 C-Lodop 演示中的条码内容类型：二维码（QRCode/PDF417/DataMatrix/Aztec）
    /// 与主流一维码（Code128 系列 / EAN / UPC / Code39 / Code93 / ITF / Codabar）。
    /// 生成 PNG 文件，供 SumatraPDF 直接打印（打印质量由矢量条模 × 整数倍缩放保证）。
    /// </summary>
    public static class BarcodeGenerator
    {
        /// <summary>
        /// 生成条码 PNG。返回文件路径。
        /// </summary>
        /// <param name="content">条码内容</param>
        /// <param name="format">格式名：qr / qrcode / code128 / ean13 / code39 / pdf417 / datamatrix / aztec 等（大小写不敏感，自动归一）</param>
        /// <param name="width">目标宽度 px（默认 400）</param>
        /// <param name="height">目标高度 px（默认 400）</param>
        /// <param name="caption">可选：条码下方说明文字（默认一维码自动带内容文本）</param>
        public static string Generate(string content, string format, int width, int height, string caption)
        {
            if (string.IsNullOrEmpty(content)) throw new ArgumentException("条码内容不能为空");
            var barcodeFormat = ParseFormat(format);
            width = Math.Max(60, Math.Min(4000, width <= 0 ? 400 : width));
            height = Math.Max(60, Math.Min(4000, height <= 0 ? 400 : height));

            // 1) 编码为条模矩阵
            var writer = new BarcodeWriterGeneric
            {
                Format = barcodeFormat,
                Options = new EncodingOptions { Margin = 2 }
            };
            var matrix = writer.Encode(content);
            if (matrix == null) throw new InvalidOperationException("条码编码失败: " + barcodeFormat);

            // 2) 矩阵按整数倍缩放到目标尺寸（保证条模边缘锐利、扫码器可读）
            var module = Math.Max(1, Math.Min(
                (int)Math.Floor((double)width / matrix.Width),
                (int)Math.Floor((double)height / matrix.Height)));
            var scaledW = module * matrix.Width;
            var scaledH = module * matrix.Height;

            // 3) 布局：条码居中，一维码/显式 caption 时下方留出文字带
            var is1D = barcodeFormat != BarcodeFormat.QR_CODE && barcodeFormat != BarcodeFormat.DATA_MATRIX &&
                       barcodeFormat != BarcodeFormat.AZTEC && barcodeFormat != BarcodeFormat.PDF_417;
            var text = caption ?? (is1D ? content : null);
            var captionH = string.IsNullOrEmpty(text) ? 0 : 34;

            using (var bmp = new Bitmap(width, height + captionH, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                var offsetX = (width - scaledW) / 2;
                var offsetY = (height - scaledH) / 2;
                using (var black = new SolidBrush(Color.Black))
                {
                    for (int y = 0; y < matrix.Height; y++)
                        for (int x = 0; x < matrix.Width; x++)
                            if (matrix[x, y])
                                g.FillRectangle(black, offsetX + x * module, offsetY + y * module, module, module);
                }

                if (captionH > 0)
                {
                    using (var font = new Font("Consolas", 11F))
                    using (var brush = new SolidBrush(Color.Black))
                    {
                        var size = g.MeasureString(text, font);
                        g.DrawString(text, font, brush, (width - size.Width) / 2, height + 6);
                    }
                }

                var path = Path.Combine(DuoAssistant.Services.SettingsStore.Instance.TempDir,
                    "barcode_" + Guid.NewGuid().ToString("N") + ".png");
                bmp.Save(path, ImageFormat.Png);
                return path;
            }
        }

        /// <summary>支持的格式名 → ZXing BarcodeFormat（别名大小写不敏感）。</summary>
        internal static BarcodeFormat ParseFormat(string format)
        {
            var f = (format ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            switch (f)
            {
                case "":
                case "qr":
                case "qrcode":
                    return BarcodeFormat.QR_CODE;
                case "pdf417":
                    return BarcodeFormat.PDF_417;
                case "datamatrix":
                    return BarcodeFormat.DATA_MATRIX;
                case "aztec":
                    return BarcodeFormat.AZTEC;
                case "code128":
                case "128":
                case "128auto":
                    return BarcodeFormat.CODE_128;
                case "code39":
                case "39":
                    return BarcodeFormat.CODE_39;
                case "code93":
                case "93":
                    return BarcodeFormat.CODE_93;
                case "ean13":
                    return BarcodeFormat.EAN_13;
                case "ean8":
                    return BarcodeFormat.EAN_8;
                case "upca":
                    return BarcodeFormat.UPC_A;
                case "itf":
                case "25interleaved":
                case "interleaved2of5":
                    return BarcodeFormat.ITF;
                case "codabar":
                    return BarcodeFormat.CODABAR;
                case "msi":
                    return BarcodeFormat.MSI;
                default:
                    throw new ArgumentException(
                        "不支持的条码格式: " + format + "（支持 qr/code128/code39/code93/ean13/ean8/upca/itf/codabar/msi/pdf417/datamatrix/aztec）");
            }
        }
    }
}

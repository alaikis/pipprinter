// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>
    /// ESC/POS 指令构造器。热敏小票/标签打印机通常支持 ESC/POS 指令集，
    /// 直接发送字节流可避免驱动排版差异，且支持中文（GB2312 编码）。
    /// </summary>
    public sealed class EscPos
    {
        private readonly List<byte> _buffer = new List<byte>();
        private readonly string _encoding;

        /// <summary>
        /// .NET Core / .NET 5+ 默认只带 UTF-8 / UTF-16 / ASCII，
        /// GB2312 / GBK / Big5 等代码页编码必须显式注册 CodePagesEncodingProvider 才可用，
        /// 否则 Encoding.GetEncoding("GB2312") 抛异常，中文会被替换成 '?'。
        /// </summary>
        static EscPos()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { /* 注册失败时按 ASCII 回退 */ }
        }

        public EscPos(string encoding = "GB2312")
        {
            _encoding = ResolveEncoding(encoding);
        }

        /// <summary>校验并返回可用的编码名；不可用时回退 ASCII。</summary>
        private static string ResolveEncoding(string name)
        {
            var want = string.IsNullOrWhiteSpace(name) ? "GB2312" : name.Trim();
            try
            {
                Encoding.GetEncoding(want);
                return want;
            }
            catch
            {
                try { Encoding.GetEncoding("GB2312"); return "GB2312"; }
                catch { return "ascii"; }
            }
        }

        private byte[] Enc(string s)
        {
            try { return Encoding.GetEncoding(_encoding).GetBytes(s ?? ""); }
            catch { return Encoding.ASCII.GetBytes(s ?? ""); }
        }

        /// <summary>初始化打印机（清缓冲/复位）。</summary>
        public EscPos Initialize()
        {
            _buffer.AddRange(new byte[] { 0x1B, 0x40 });
            return this;
        }

        /// <summary>对齐：0 左，1 居中，2 右。</summary>
        public EscPos Align(int mode)
        {
            _buffer.AddRange(new byte[] { 0x1B, 0x61, (byte)Math.Max(0, Math.Min(2, mode)) });
            return this;
        }

        /// <summary>加粗开关。</summary>
        public EscPos Bold(bool on)
        {
            _buffer.AddRange(new byte[] { 0x1B, 0x45, (byte)(on ? 1 : 0) });
            return this;
        }

        /// <summary>走纸 n 行。</summary>
        public EscPos Feed(int lines = 1)
        {
            for (int i = 0; i < Math.Max(0, lines); i++) _buffer.Add(0x0A);
            return this;
        }

        public EscPos Text(string s)
        {
            _buffer.AddRange(Enc(s));
            return this;
        }

        public EscPos TextLine(string s)
        {
            _buffer.AddRange(Enc(s));
            _buffer.Add(0x0A);
            return this;
        }

        /// <summary>一维条码。format: code128 / code39 / ean13 / ean8 / upca / itf / codabar。</summary>
        public EscPos Barcode(string value, string format = "code128", int height = 50, int width = 2)
        {
            if (string.IsNullOrEmpty(value)) return this;
            var f = (format ?? "code128").ToLowerInvariant();

            _buffer.AddRange(new byte[] { 0x1D, 0x77, (byte)Math.Max(2, Math.Min(6, width)) });   // GS w 条宽
            _buffer.AddRange(new byte[] { 0x1D, 0x68, (byte)Math.Max(1, Math.Min(255, height)) }); // GS h 条高
            _buffer.AddRange(new byte[] { 0x1D, 0x48, 0x02 });                                    // GS H 2 条码下方打印

            byte[] data;
            byte mode;
            switch (f)
            {
                case "ean13": mode = 67; data = Enc(value); break;
                case "ean8": mode = 68; data = Enc(value); break;
                case "upca": mode = 65; data = Enc(value); break;
                case "code39": mode = 69; data = Enc(value); break;
                case "itf": mode = 70; data = Enc(value); break;
                case "codabar": mode = 71; data = Enc(value); break;
                default:
                    mode = 73; // CODE128：需带子集前缀 {B
                    data = Enc("{B" + value);
                    break;
            }
            _buffer.Add(0x1D); _buffer.Add(0x6B); _buffer.Add(mode);
            _buffer.Add((byte)data.Length);
            _buffer.AddRange(data);
            _buffer.Add(0x0A);
            return this;
        }

        /// <summary>QR 二维码（模型 2，纠错 M，模块 4）。</summary>
        public EscPos QrCode(string value, int size = 4)
        {
            if (string.IsNullOrEmpty(value)) return this;
            var data = Enc(value);

            _buffer.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x43, 0x32 });       // 模型 2
            _buffer.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });       // 纠错 M
            _buffer.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x47,
                                          (byte)Math.Max(1, Math.Min(16, size)) });                // 模块尺寸

            var len = data.Length + 3;
            _buffer.AddRange(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8),
                                          0x31, 0x50, 0x30 });
            _buffer.AddRange(data);
            _buffer.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });       // 打印
            _buffer.Add(0x0A);
            return this;
        }

        /// <summary>切刀。mode: full / partial / feed-full / feed-partial。</summary>
        public EscPos Cut(string mode = "full")
        {
            byte m;
            switch ((mode ?? "full").ToLowerInvariant())
            {
                case "partial": m = 1; break;
                case "feed-full": m = 65; break;
                case "feed-partial": m = 66; break;
                default: m = 0; break;
            }
            _buffer.AddRange(new byte[] { 0x1D, 0x56, m });
            return this;
        }

        /// <summary>开钱箱（DK1 脉冲）。</summary>
        public EscPos CashDrawer(int pin = 0)
        {
            _buffer.AddRange(new byte[] { 0x1B, 0x70, (byte)(pin == 0 ? 0 : 1), 0x32, 0x32 });
            return this;
        }

        public byte[] ToArray() => _buffer.ToArray();
    }

    /// <summary>
    /// ESC/POS 字节流输出：支持 USB/并口/共享打印机（Win32 RAW 数据类型）
    /// 与 TCP 9100 网络打印机。
    /// </summary>
    public class EscPosService
    {
        #region Win32 原始打印

        [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool OpenPrinter(string printerName, out IntPtr phPrinter, IntPtr pd);

        [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOA di);

        [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        /// <summary>通过 Win32 RAW 数据类型把字节流写入指定打印机。</summary>
        public PrintOutcome PrintRaw(string printer, byte[] data, string docName = "LycheePrintingAssistant")
        {
            if (data == null || data.Length == 0)
                return new PrintOutcome { Ok = false, Message = "ESC/POS 数据为空" };

            var name = NativePrintService.ResolvePrinterName(printer);
            if (string.IsNullOrEmpty(name))
                return new PrintOutcome { Ok = false, Message = "没有可用的打印机" };

            IntPtr hPrinter = IntPtr.Zero;
            try
            {
                if (!OpenPrinter(name, out hPrinter, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    return new PrintOutcome
                    {
                        Ok = false, Message = $"打开打印机失败 ({name})，Win32 错误 {err}"
                    };
                }

                var di = new DOCINFOA
                {
                    pDocName = docName ?? "Print",
                    pOutputFile = null,
                    pDataType = "RAW"
                };
                if (!StartDocPrinter(hPrinter, 1, ref di))
                    return new PrintOutcome { Ok = false, Message = "StartDocPrinter 失败" };

                try
                {
                    if (!StartPagePrinter(hPrinter))
                        return new PrintOutcome { Ok = false, Message = "StartPagePrinter 失败" };

                    IntPtr pBytes = Marshal.AllocHGlobal(data.Length);
                    try
                    {
                        Marshal.Copy(data, 0, pBytes, data.Length);
                        if (!WritePrinter(hPrinter, pBytes, data.Length, out int written))
                        {
                            var err = Marshal.GetLastWin32Error();
                            return new PrintOutcome
                            {
                                Ok = false, Message = $"写入打印机失败，Win32 错误 {err}"
                            };
                        }
                    }
                    finally { Marshal.FreeHGlobal(pBytes); }
                }
                finally
                {
                    try { EndPagePrinter(hPrinter); } catch { /* 忽略 */ }
                    try { EndDocPrinter(hPrinter); } catch { /* 忽略 */ }
                }

                return new PrintOutcome
                {
                    Ok = true, Engine = "ESC/POS",
                    Message = "已通过 RAW 数据类型提交到 " + name,
                    Detail = $"{data.Length} 字节"
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("ESC/POS 打印失败: " + ex);
                return new PrintOutcome { Ok = false, Message = "ESC/POS 打印失败: " + ex.Message };
            }
            finally
            {
                if (hPrinter != IntPtr.Zero)
                    try { ClosePrinter(hPrinter); } catch { /* 忽略 */ }
            }
        }

        #endregion

        #region TCP 9100 网络打印机

        /// <summary>网络打印机（host:port，默认 9100）直接发送字节流。</summary>
        public PrintOutcome PrintTcp(string host, int port, byte[] data, int timeoutMs = 8000)
        {
            if (data == null || data.Length == 0)
                return new PrintOutcome { Ok = false, Message = "ESC/POS 数据为空" };
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    if (!connect.Wait(timeoutMs))
                        return new PrintOutcome { Ok = false, Message = $"连接打印机超时 {host}:{port}" };
                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = timeoutMs;
                        stream.ReadTimeout = timeoutMs;
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                    }
                }
                return new PrintOutcome
                {
                    Ok = true, Engine = "ESC/POS/TCP",
                    Message = $"已发送到网络打印机 {host}:{port}",
                    Detail = $"{data.Length} 字节"
                };
            }
            catch (Exception ex)
            {
                return new PrintOutcome { Ok = false, Message = "网络打印机发送失败: " + ex.Message };
            }
        }

        /// <summary>解析 "host:port" / "tcp://host:port"；无端口时用 9100。</summary>
        public static bool TryParseNetwork(string target, out string host, out int port)
        {
            host = "";
            port = 9100;
            if (string.IsNullOrWhiteSpace(target)) return false;

            var s = target.Trim();
            if (s.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
            var idx = s.LastIndexOf(':');
            if (idx <= 0 || idx == s.Length - 1) return false;
            host = s.Substring(0, idx);
            return int.TryParse(s.Substring(idx + 1), out port) && port > 0 && port <= 65535;
        }

        #endregion

        /// <summary>统一入口：识别网络打印机或本地打印机。</summary>
        public PrintOutcome Print(string printer, byte[] data)
        {
            if (!string.IsNullOrWhiteSpace(printer) && TryParseNetwork(printer, out var host, out var port))
                return PrintTcp(host, port, data);
            return PrintRaw(printer, data);
        }

        /// <summary>把 ESC/POS 字节流保存为文件（调试/驱动对接用）。</summary>
        public string SaveToFile(byte[] data)
        {
            var path = Path.Combine(SettingsStore.Instance.TempDir,
                "escpos_" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, data);
            return path;
        }
    }
}

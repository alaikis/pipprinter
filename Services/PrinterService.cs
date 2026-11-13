using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DuoAssistant.Services
{
    public class PrinterInfo
    {
        public string Name { get; set; } = "";
        public string Driver { get; set; } = "未知";
        public string Port { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool IsValid { get; set; }
        public string Status { get; set; } = "就绪";
        public string PaperSizes { get; set; } = "";
    }

    /// <summary>本机打印机枚举、默认打印机切换与测试页。</summary>
    public class PrinterService
    {
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDefaultPrinter(string name);

        public List<string> GetNames()
        {
            var list = new List<string>();
            foreach (string name in PrinterSettings.InstalledPrinters) list.Add(name);
            return list;
        }

        public string GetDefault()
        {
            try { return new PrinterSettings().PrinterName; }
            catch { return ""; }
        }

        public List<PrinterInfo> GetInstalled()
        {
            var result = new List<PrinterInfo>();
            var def = GetDefault();
            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                var info = new PrinterInfo { Name = name, IsDefault = name == def };
                try
                {
                    var ps = new PrinterSettings { PrinterName = name };
                    info.IsValid = ps.IsValid;
                    info.Status = ps.IsValid ? "就绪" : "不可用";
                    var sizes = new List<string>();
                    foreach (PaperSize s in ps.PaperSizes) sizes.Add(s.PaperName);
                    info.PaperSizes = string.Join(", ", sizes.Count > 4 ? sizes.GetRange(0, 4) : sizes);
                }
                catch (Exception ex)
                {
                    info.Status = "读取失败: " + ex.Message;
                }
                ReadRegistryDetail(info);
                result.Add(info);
            }
            return result;
        }

        private static void ReadRegistryDetail(PrinterInfo info)
        {
            try
            {
                var keyPath = @"SYSTEM\CurrentControlSet\Control\Print\Printers\" + info.Name;
                using (var key = Registry.LocalMachine.OpenSubKey(keyPath, false))
                {
                    if (key == null) return;
                    info.Driver = key.GetValue("Printer Driver") as string ?? info.Driver;
                    info.Port = key.GetValue("Port") as string ?? "";
                }
            }
            catch { /* 无权限时忽略，保留默认值 */ }
        }

        public bool SetDefault(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (GetNames().FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) < 0)
                return false;
            return SetDefaultPrinter(name);
        }

        /// <summary>打印一张带时间与服务信息的测试页。</summary>
        public bool PrintTestPage(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = GetDefault();
            try
            {
                using (var doc = new PrintDocument())
                {
                    doc.PrinterSettings.PrinterName = name;
                    if (!doc.PrinterSettings.IsValid) return false;
                    doc.DocumentName = DuoAssistant.Utils.Brand.Title + " 测试页";
                    doc.PrintPage += (s, e) =>
                    {
                        var g = e.Graphics;
                        using (var title = new Font("Microsoft YaHei", 20, FontStyle.Bold))
                        using (var body = new Font("Microsoft YaHei", 11))
                        using (var brush = new SolidBrush(Color.Black))
                        {
                            g.DrawString(DuoAssistant.Utils.Brand.Title, title, brush, 60, 60);
                            g.DrawString("测试页 / Test Page", body, brush, 60, 105);
                            g.DrawString("打印机: " + name, body, brush, 60, 140);
                            g.DrawString("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), body, brush, 60, 165);
                            g.DrawString("若您看到此页，说明本机打印链路正常。", body, brush, 60, 190);
                            g.DrawRectangle(new Pen(Color.Gray), 55, 55, 480, 160);
                        }
                        e.HasMorePages = false;
                    };
                    doc.Print();
                }
                return true;
            }
            catch (Exception ex)
            {
                DuoAssistant.Utils.Tools.Logger("测试页打印失败: " + ex.Message);
                return false;
            }
        }
    }
}

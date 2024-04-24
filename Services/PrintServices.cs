using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Net;
using System.Reflection;
using log4net;
using System.Diagnostics;
using System.Drawing.Printing;
using Newtonsoft.Json.Linq;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    class PrintServices
    {
        private ILog logging = LogManager.GetLogger("");


        [DllImport("winspool.drv")]
        public static extern bool SetDefaultPrinter(String Name);


        /// <summary>
        /// 
        ///默认文件存放地址
        /// </summary>
        /// <returns></returns>
        private String Tempdir()
        {
            String filePath = String.Format("{0}{1}", AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            return filePath;
        }
        /// <summary>
        /// 打印PDF网络文件
        /// </summary>
        /// <param name="label"></param>
        /// <returns></returns>
        public dynamic PrintPdf(String label)
        {
            (new DriverCheck()).CheckPdfPrinterExist();
            JObject dt = (JObject)JsonConvert.DeserializeObject(label);
            String labelurl = (string)dt["label"];
            if (!labelurl.StartsWith("http"))
            {
                labelurl = "https:" + labelurl;
            }
            HttpClient cl = new HttpClient();
            var msg = cl.GetByteArrayAsync(labelurl).Result;
            String path = Tempdir();
            String fileName = System.Guid.NewGuid().ToString("N") + ".pdf";
            String file = path + "\\" + fileName;
            FileStream fs = new System.IO.FileStream(file, System.IO.FileMode.CreateNew);
            fs.Write(msg, 0, msg.Length);
            fs.Close();
            
            PrintDocument doc = new PrintDocument();
            string primaryPrinterName = doc.PrinterSettings.PrinterName;
            string printer = primaryPrinterName;
            if (dt.ContainsKey("printer"))
            {
                printer = (string)dt["printer"];//打印机名称
            }
            int copies = 1;
            if (dt.ContainsKey("copies"))
            {
                copies = (int)dt["copies"];
            }
            String fit = "fit";//⽆边框 noscale/缩⼩到合适⼤⼩（默认）shrink/合适⼤⼩ fit/compat 兼容
            if (dt.ContainsKey("fit"))
            {
                fit = (string)dt["fit"];
            }
            String scale = "portrait"; //portrait:纵向 ,landscape:横向
            if (dt.ContainsKey("scale"))
            {
                scale = (string)dt["scale"];
            }
            
            String paperSize = "";
            if (dt.ContainsKey("paperKind"))
            {
                paperSize = String.Format("paper=%s",(string)dt["paperKind"]);
            }
            string printPages = "1-";
            if (dt.ContainsKey("printPages"))
            {
                printPages = (string)dt["printPages"];
            }
            String cmdLine = string.Format("-print-settings {0},{1},{2},{3},{4}x",fit,scale,printPages,paperSize,copies);
            try
            {
                string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib\\driver","SumatraPDF.exe"); 
                Debug.WriteLine(app);
                string args = string.Format("-silent {0}  -zoom \"fit width\" -print-to \"{1}\" -exit-when-done -fullscreen \"{2}\"",cmdLine, printer, file);
                Debug.WriteLine(args);
                Process p = new Process();
                p.StartInfo = new ProcessStartInfo()
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = app,
                    Arguments = args
                };
                p.Start();

            }
            finally
            {
                //恢复默认打印机
                SetDefaultPrinter(primaryPrinterName);
            }
            return Result.success(data: label, cmd: "Print",msg: "print successful!");
        }

        /**
         * 获得打印机列表
         */
        public dynamic GetPrinterList(string args)
        {
            List<string> lt = new List<string>();
            PrinterSettings printerSettings = new PrinterSettings();
            foreach (string printerName in PrinterSettings.InstalledPrinters)
            {
                lt.Add(printerName);
            }
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("printers", lt);
            rd.Add("primary", printerSettings.PrinterName);
            rd.Add("cmd","GetPrinterList");
            return Result.success(data:rd,cmd:"getPrinterList");
        }


        /// <summary>
        /// 读取PDF打印驱动配置信息
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public dynamic GetPdfDriverConfig(string args)
        {
            String config = "";
            string targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib\\driver","SumatraPDF-settings.txt");
            if (File.Exists(targetFile))
            {
                try
                {
                    using (FileStream fs = File.Open(targetFile, FileMode.Open, FileAccess.Read))
                    {
                        using (StreamReader reader = new StreamReader(fs))
                        {
                            config = reader.ReadToEnd(); // 读取文件的全部内容
                        }
                    }
                }
                catch (IOException ioEx)
                {
                    Tools.Logger("IO Exception: " + ioEx.Message);
                }
                catch (Exception ex)
                {
                    Tools.Logger("Exception: " + ex.Message);
                }
            }
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("config", config);
            rd.Add("cmd","GetPdfDriverConfig");
            return Result.success(data:rd,cmd:"GetPdfDriverConfig");
        }

        /// <summary>
        /// 更新PDF打印机配置信息
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public dynamic SetPdfPrintDriverConfig(string args)
        {
            string targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib\\driver","SumatraPDF-settings.txt");
            JObject data = (JObject)JsonConvert.DeserializeObject(args);
            if (data.ContainsKey("config"))
            {
                using (StreamWriter sw = new StreamWriter(targetFile))
                {
                    sw.Write((string)data["config"]);
                }
            }
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("message", "the config has updated");
            rd.Add("cmd","SetPdfPrintDriverConfig");
            return Result.success(data:rd,cmd:"SetPdfPrintDriverConfig");
        }

        /**
         * 模板打印
         */
        public dynamic TemplatePrint(String args)
        {
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("message", "the config has updated");
            rd.Add("cmd","TemplatePrint");
            return Result.success(data:rd,cmd:"TemplatePrint");
        }
    }
    
    
}

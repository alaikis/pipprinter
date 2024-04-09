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
using System.Drawing.Printing;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    class PrintServices
    {
        ILog logging = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


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
            
            JObject dt = (JObject)JsonConvert.DeserializeObject(label);
            String labelurl = (string)dt["label"];
            HttpClient cl = new HttpClient();
            var msg = cl.GetByteArrayAsync(labelurl).Result;
            String path = Tempdir();
            String fileName = System.Guid.NewGuid().ToString("N") + ".pdf";
            String file = path + "\\" + fileName;
            Console.WriteLine(file);
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
            Debug.WriteLine(printer);
            int startPage = 1;
            String cmdLine = string.Format("-print-settings {0},{1},{2}-,paperkind=258,{3}x",fit,scale,startPage,copies);
            try
            {
                string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pdfreader","SumatraPDF.exe"); //String.Format("{0}\SumatraPDF.exe", AppDomain.CurrentDomain.BaseDirectory); //Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SumatraPDF.exe");
                Debug.WriteLine(app);
                string args = string.Format("-silent {0} -exit-on-print -print-to \"{1}\" -exit-on-print \"{2}\"",cmdLine, printer, file);
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
            return Result.success(data: label, cmd: "printer",msg: "print successful!");
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
            return Result.success(data:rd,cmd:"getPrinterList");
        }
    }
    
    
}

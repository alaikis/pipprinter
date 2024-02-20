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


            Debug.WriteLine("接收到");

            logging.Debug("start pdf print");
            dynamic dt = JsonConvert.DeserializeObject(label);
            String labelurl = dt["label"];
            HttpClient cl = new HttpClient();
            var msg = cl.GetByteArrayAsync(labelurl).Result;
            logging.Debug("get the stream");
            String path = Tempdir();
            String fileName = System.Guid.NewGuid().ToString("N") + ".pdf";
            String file = path + "\\" + fileName;
            logging.Debug(file);
            Console.WriteLine(file);
            FileStream fs = new System.IO.FileStream(file, System.IO.FileMode.CreateNew);
            fs.Write(msg, 0, msg.Length);
            fs.Close();

            PrintDocument doc = new PrintDocument();
            string primaryPrinterName = doc.PrinterSettings.PrinterName;
            string printer = primaryPrinterName;
            Debug.WriteLine(printer);
            try
            {
                String queryPrinter = dt["printer"];
                Debug.WriteLine(String.Format("请求打印机:{0}", queryPrinter));
                if (queryPrinter!=null)
                {
                    printer = queryPrinter;
                }
            }
            catch (Exception e)
            {
                logging.Debug(e.ToString());
            }
            Debug.WriteLine(printer);
            try
            {
                string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pdfreader","SumatraPDF.exe"); //String.Format("{0}\SumatraPDF.exe", AppDomain.CurrentDomain.BaseDirectory); //Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SumatraPDF.exe");
                Debug.WriteLine(app);
                string args = string.Format("-silent -exit-on-print -print-to \"{0}\" \"{1}\"", printer, file);
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


            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("code", 200);
            rd.Add("message", "打印成功!");
            rd.Add("uuid", dt["uuid"]);
            rd.Add("operator", "printer");
            return JsonConvert.SerializeObject(rd);
        }
    }
}

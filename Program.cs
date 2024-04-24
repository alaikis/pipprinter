using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DuoAssistant.Services;
using System.Reflection;
using log4net;
using log4net.Config;
using log4net.Repository;

namespace DuoAssistant
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ILoggerRepository repository = LogManager.CreateRepository("");
            XmlConfigurator.Configure(repository, new FileInfo("Config/log4net.config"));
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Home());

        }
    }
}

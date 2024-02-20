using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using log4net;

namespace DuoAssistant.Services
{
    class BaseServices
    {
        ILog logging = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        /// <summary>
        /// 是否设置为开机启动
        /// </summary>
        public void StartWhenSystemStart()
        {
            bool _exist = false;
            String appName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name.ToString();
            try
            {
                RegistryKey local = Registry.LocalMachine;
                RegistryKey runs = local.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (runs == null)
                {
                    RegistryKey key2 = local.CreateSubKey("SOFTWARE");
                    RegistryKey key3 = key2.CreateSubKey("Microsoft");
                    RegistryKey key4 = key3.CreateSubKey("Windows");
                    RegistryKey key5 = key4.CreateSubKey("CurrentVersion");
                    RegistryKey key6 = key5.CreateSubKey("Run");
                    runs = key6;
                }
                string[] runsName = runs.GetValueNames();
                foreach (string strName in runsName)
                {
                    if (strName ==appName)
                    {
                        _exist = true;
                    }
                }

            }
            catch
            {
                _exist = false;
            }
            if (!_exist)
            {
                String softpath = AppDomain.CurrentDomain.BaseDirectory + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name.ToString();
                try
                {
                    RegistryKey local = Registry.LocalMachine;
                    RegistryKey key = local.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                    if (key == null)
                    {
                        local.CreateSubKey("SOFTWARE//Microsoft//Windows//CurrentVersion//Run");
                    }
                    key.SetValue(appName, softpath);
                    key.Close();
                }
                catch (Exception e)
                {
                    logging.Debug(e.Message.ToString());
                    //throw;
                }
            }
        }
    }
}

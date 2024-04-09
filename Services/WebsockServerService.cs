using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Fleck;
using Newtonsoft.Json;
using log4net;
using System.Diagnostics;
using DuoAssistant.Services;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    class WebsockServerService
    {
        ILog logging = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// 开启websocket
        /// </summary>
        public void StartNewWebSocket()
        {

            var allSockers = new List<IWebSocketConnection>();
            WebSocketServer websock = new WebSocketServer("ws://0.0.0.0:19860");
            websock.RestartAfterListenError = true;
            websock.Start(sk =>
            {
                sk.OnOpen = () =>
                {
                    logging.Debug("Printer connect success!");
                    allSockers.Add(sk);
                };
                sk.OnClose = () =>
                {
                    allSockers.Remove(sk);
                };
                sk.OnMessage = message =>
                {
                    Dictionary<String, dynamic> startMsg = new Dictionary<string, dynamic>();
                    sk.Send(JsonConvert.SerializeObject(Result.success(msg:"connect the print successful",cmd:"startPrint")));
                    PrintServices s = new PrintServices();
                    var t = s.GetType();
                    Object obj = Activator.CreateInstance(t);
                    try
                    {
                        dynamic jsondata = JsonConvert.DeserializeObject(message);
                        String methodName = jsondata["cmd"].ToString();
                        MethodInfo method = t.GetMethod(methodName);
                        Object[] paras = new Object[] { message.ToString() };
                        dynamic redata = method.Invoke(obj, paras);
                        sk.Send(redata);
                    }
                    catch (Exception ex)
                    {
                        logging.Debug(ex.StackTrace);
                        Debug.WriteLine(ex.StackTrace);
                        sk.Send(JsonConvert.SerializeObject(Result.error(ex.Message.ToString(), cmd: "notify")));
                    }
                };
            });
        }
    }
}

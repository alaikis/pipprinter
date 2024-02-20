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
                    Console.WriteLine("Close!");
                    allSockers.Remove(sk);
                };
                sk.OnMessage = message =>
                {
                    logging.Debug(message);
                    Dictionary<String, dynamic> startMsg = new Dictionary<string, dynamic>();
                    startMsg.Add("code", 200);
                    startMsg.Add("message", "打印客服端连接成功");
                    startMsg.Add("cmd", "startPrint");
                    sk.Send(JsonConvert.SerializeObject(startMsg));
                    PrintServices s = new PrintServices();
                    var t = s.GetType();
                    Object obj = Activator.CreateInstance(t);
                    try
                    {
                        dynamic jsondata = JsonConvert.DeserializeObject(message);
                        String methodName = jsondata["cmd"].ToString();
                        logging.Debug("反射方法名称：" + methodName);
                        MethodInfo method = t.GetMethod(methodName);
                        Object[] paras = new Object[] { message.ToString() };
                        dynamic redata = method.Invoke(obj, paras);
                        sk.Send(redata);
                    }
                    catch (Exception ex)
                    {
                        logging.Debug(ex.StackTrace);
                        Debug.WriteLine(ex.StackTrace);
                        Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
                        rd.Add("code", 500);
                        rd.Add("message", ex.Message.ToString());
                        rd.Add("cmd", "notify");
                        sk.Send(JsonConvert.SerializeObject(rd));
                    }


                };
            });
        }
    }
}

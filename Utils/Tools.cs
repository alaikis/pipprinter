using System;
using log4net;
namespace DuoAssistant.Utils;

public class Tools
{
    
    public static void Logger(String text)
    {
        text = String.Format("{0}\r\n", text);
        ILog logging = LogManager.GetLogger("");
        Console.Write(text);
        logging.Debug(text);
    }
}
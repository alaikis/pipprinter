// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
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
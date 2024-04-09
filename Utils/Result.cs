using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Microsoft.VisualBasic;

namespace DuoAssistant.Utils
{
    public class Result
    {
        public static dynamic success(dynamic data=null,string cmd=null,dynamic msg=null)
        {
            TimeSpan ts = DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("code", 200);
            rd.Add("timestamp", Convert.ToInt64(ts.TotalSeconds).ToString());
            rd.Add("data",data);
            rd.Add("error",msg? msg : "success");
            rd.Add("operator", cmd);
            rd.Add("uuid", System.Guid.NewGuid().ToString("N"));
            return JsonConvert.SerializeObject(rd); 
        }
        
        public static dynamic error(dynamic errors,int code = 400,string cmd=null,dynamic data=null)
        {
            TimeSpan ts = DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            Dictionary<String, dynamic> rd = new Dictionary<string, dynamic>();
            rd.Add("code", code);
            rd.Add("timestamp", Convert.ToInt64(ts.TotalSeconds).ToString());
            rd.Add("data",data);
            rd.Add("error",errors);
            rd.Add("operator", cmd);
            rd.Add("uuid", System.Guid.NewGuid().ToString("N"));
            return JsonConvert.SerializeObject(rd); 
        }
    }
}
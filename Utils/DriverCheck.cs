using System;
using System.Collections.Generic;
using log4net;
using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace DuoAssistant.Utils;

public class DriverCheck
{
    private ILog logging = LogManager.GetLogger("");
    public async void CheckPdfPrinterExist()
    {
        string _directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib\\driver");
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory);
        }
        string savePath = Path.Combine(_directory,"SumatraPDF.zip");
        string targetFile = Path.Combine(_directory, "SumatraPDF.exe");
        Tools.Logger("开始检测打印插件:"+targetFile);
        if (!File.Exists(targetFile))
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Accept","application/vnd.github+json");
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version","2022-11-28");
            httpClient.DefaultRequestHeaders.Add("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0");
            try
            {
                Tools.Logger("打印插件不存在");
                String versionCheckUrl = "https://api.github.com/repos/sumatrapdfreader/sumatrapdf/releases/latest";
                String restString = httpClient.GetAsync(versionCheckUrl).Result.Content.ReadAsStringAsync().Result;
                JObject repositoryData = (JObject)JsonConvert.DeserializeObject(restString);
                String tagName = (string)repositoryData["tag_name"];
                Tools.Logger("版本标签："+tagName);
                Regex regex = new Regex("[0-9]+\\.[0-9]+\\.[0-9]+");
                var versionRegex = regex.Matches(tagName);
                List<String> versions = new List<string>();
                foreach (Match  item in versionRegex)
                {
                    versions.Add(item.Value);
                }
                Tools.Logger(versions.Count.ToString());
                if (versions.Count > 0)
                {
                    // String zipUrl = (string)repositoryData["zipball_url"];
                    String exeFileName = String.Format("SumatraPDF-{0}-64.exe", versions[0]);
                    String zipUrl = String.Format("https://www.sumatrapdfreader.org/dl/rel/{0}/SumatraPDF-{0}-64.zip",versions[0]);
                    String tmpFile = Path.Combine(_directory, exeFileName);
                    Tools.Logger(tmpFile);
                    Tools.Logger(zipUrl);
                    if (File.Exists(savePath))
                    {
                        File.Delete(savePath);
                        Tools.Logger("旧的驱动文件已经删除");
                    }
                    
                    if (File.Exists(tmpFile))
                    {
                        File.Delete(tmpFile);
                        Tools.Logger("存在旧的驱动器已经删除");
                    }

                    var contentStream = httpClient.GetAsync(zipUrl).Result.Content.ReadAsStreamAsync().Result;
                    var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.Delete, 4096, true);
                    await contentStream.CopyToAsync(fileStream);
                    contentStream.Close();
                    fileStream.Close();
                    Tools.Logger("开始解压并配置");
                    ZipFile.ExtractToDirectory(savePath,_directory);
                    File.Move(tmpFile,targetFile);
                }
                else
                {
                    Tools.Logger("未发现可用的驱动版本");
                }
                
            }
            catch(Exception e)
            {
                Tools.Logger(e.Message.ToString());
                Tools.Logger(e.StackTrace);
            }
        }
        else
        {
            Tools.Logger("the pdf print driver is exist");
        }
        
    }
}
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// PDF 打印驱动（SumatraPDF）检测与恢复。
    /// 优先使用随包资源，避免每次冷启动都访问 GitHub；联网下载仅作最后兜底。
    /// </summary>
    public class DriverCheck
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public string DriverPath => Path.Combine(
            DuoAssistant.Services.SettingsStore.Instance.DriverDir, "SumatraPDF.exe");

        /// <summary>同步确保驱动存在；返回是否可用。</summary>
        public bool EnsureDriver()
        {
            try
            {
                var target = DriverPath;
                var dir = Path.GetDirectoryName(target);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(target))
                {
                    Tools.Logger("PDF 打印驱动已存在: " + target);
                    return true;
                }

                // 1) 随包资源
                var bundled = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pdfreader", "SumatraPDF.exe");
                if (File.Exists(bundled))
                {
                    File.Copy(bundled, target, true);
                    Tools.Logger("已从随包资源恢复 PDF 打印驱动");
                    return true;
                }

                // 2) 联网下载最新 release
                Tools.Logger("驱动缺失，尝试在线获取 SumatraPDF …");
                return DownloadLatest(target);
            }
            catch (Exception ex)
            {
                Tools.Logger("驱动恢复失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>保留旧入口名，供原反射协议与旧代码调用。</summary>
        public void CheckPdfPrinterExist() => EnsureDriver();

        private bool DownloadLatest(string target)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/sumatrapdfreader/sumatrapdf/releases/latest");
                req.Headers.Add("Accept", "application/vnd.github+json");
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.Add("User-Agent", "PipPrinter/2.0");

                var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return false;
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var tag = (string)((JObject)JsonConvert.DeserializeObject(json))["tag_name"];
                var match = Regex.Match(tag ?? "", @"\d+\.\d+(\.\d+)?");
                if (!match.Success) return false;

                var version = match.Value;
                var zipUrl = string.Format(
                    "https://www.sumatrapdfreader.org/dl/rel/{0}/SumatraPDF-{0}-64.zip", version);
                var zipPath = Path.Combine(Path.GetDirectoryName(target), "SumatraPDF.zip");
                var bytes = Http.GetByteArrayAsync(zipUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(zipPath, bytes);

                var extractDir = Path.Combine(Path.GetDirectoryName(target), "_extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir, true);

                foreach (var f in Directory.GetFiles(extractDir, "SumatraPDF*.exe", SearchOption.AllDirectories))
                {
                    File.Copy(f, target, true);
                    break;
                }
                try { Directory.Delete(extractDir, true); } catch { /* 忽略清理失败 */ }
                try { File.Delete(zipPath); } catch { /* 忽略清理失败 */ }

                var ok = File.Exists(target);
                Tools.Logger(ok ? "驱动下载完成: " + version : "驱动下载后仍未找到 exe");
                return ok;
            }
            catch (Exception ex)
            {
                Tools.Logger("在线下载驱动失败: " + ex.Message);
                return false;
            }
        }
    }
}

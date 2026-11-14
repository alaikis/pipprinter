// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 内置 PDF 组件的检测与恢复（不是打印机驱动）。
    /// 优先使用随包资源；联网下载仅作最后兜底，且下载链路依次做来源域名、
    /// 体积、ZIP 条目、PE 头与 Authenticode 签名校验，任一不通过都不启用。
    /// 客户端不下载、不安装任何打印机驱动。
    /// </summary>
    public class DriverCheck
    {
        private const long MaxDownloadBytes = 60L * 1024 * 1024;
        private const long MaxUncompressedBytes = 120L * 1024 * 1024;
        private const long MaxExeBytes = 60L * 1024 * 1024;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public string DriverPath => Path.Combine(
            DuoAssistant.Services.SettingsStore.Instance.DriverDir, "SumatraPDF.exe");

        /// <summary>同步确保内置 PDF 组件存在；返回是否可用。</summary>
        public bool EnsureDriver()
        {
            try
            {
                var target = DriverPath;
                var dir = Path.GetDirectoryName(target);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(target))
                {
                    Tools.Logger("内置 PDF 组件已存在: " + target);
                    return true;
                }

                // 1) 随包资源
                var bundled = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pdfreader", "SumatraPDF.exe");
                if (File.Exists(bundled))
                {
                    File.Copy(bundled, target, true);
                    Tools.Logger("已从随包资源恢复内置 PDF 组件");
                    return true;
                }

                // 2) 官方渠道下载（带完整校验）
                Tools.Logger("内置 PDF 组件缺失，尝试从官方渠道获取 …");
                return DownloadLatest(target);
            }
            catch (Exception ex)
            {
                Tools.Logger("内置 PDF 组件恢复失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>保留旧入口名，供原反射协议与旧代码调用。</summary>
        public void CheckPdfPrinterExist() => EnsureDriver();

        private bool DownloadLatest(string target)
        {
            string pending = target + ".pending";
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
                // 只取开头的数字点分版本，避免把 tag 里的其他字符拼进下载 URL
                var match = Regex.Match(tag ?? "", @"^\d+\.\d+(\.\d+)?");
                if (!match.Success) return false;
                var version = match.Value;

                var zipUrl = string.Format(
                    "https://www.sumatrapdfreader.org/dl/rel/{0}/SumatraPDF-{0}-64.zip", version);
                var uri = new Uri(zipUrl);
                if (uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.Equals("www.sumatrapdfreader.org", StringComparison.OrdinalIgnoreCase))
                {
                    Tools.Logger("下载来源不在白名单，已拒绝");
                    return false;
                }

                var zipBytes = DownloadLimited(zipUrl);
                if (zipBytes == null) return false;
                if (zipBytes.Length < 4 || zipBytes[0] != 'P' || zipBytes[1] != 'K')
                {
                    Tools.Logger("下载内容不是 ZIP，已拒绝");
                    return false;
                }

                var exeBytes = ExtractExecutable(zipBytes);
                if (exeBytes == null) return false;
                if (exeBytes.Length < 2 || exeBytes[0] != 'M' || exeBytes[1] != 'Z')
                {
                    Tools.Logger("解压出的文件不是有效 PE，已拒绝");
                    return false;
                }

                File.WriteAllBytes(pending, exeBytes);
                string signer;
                if (!VerifyAuthenticode(pending, out signer))
                {
                    Tools.Logger("内置 PDF 组件签名校验未通过，已拒绝启用: " + signer);
                    return false;
                }

                var hash = Sha256Hex(exeBytes);
                File.WriteAllText(target + ".sha256",
                    hash + "  SumatraPDF.exe\r\n" + version + "\r\n" + signer + "\r\n");
                File.Copy(pending, target, true);
                Tools.Logger(string.Format("内置 PDF 组件校验通过: {0} / SHA-256 {1} / 签名 {2}",
                    version, hash, signer));
                return File.Exists(target);
            }
            catch (Exception ex)
            {
                Tools.Logger("在线获取内置 PDF 组件失败: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (File.Exists(pending)) File.Delete(pending); } catch { /* 忽略清理失败 */ }
            }
        }

        /// <summary>按 Content-Length 与实际读取字节双重限制下载体积。</summary>
        private static byte[] DownloadLimited(string url)
        {
            using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) return null;
                var declared = resp.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > MaxDownloadBytes)
                {
                    Tools.Logger("下载体积超出上限，已拒绝");
                    return null;
                }
                using (var stream = resp.Content.ReadAsStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxDownloadBytes)
                        {
                            Tools.Logger("下载体积超出上限，已拒绝");
                            return null;
                        }
                        ms.Write(buf, 0, n);
                    }
                    return ms.ToArray();
                }
            }
        }

        /// <summary>不落盘解压；校验条目路径与累计体积后，只取第一个 exe。</summary>
        private static byte[] ExtractExecutable(byte[] zipBytes)
        {
            using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    var full = entry.FullName.Replace('\\', '/');
                    if (full.StartsWith("/", StringComparison.Ordinal) || full.Contains("..") ||
                        full.Contains(':') || Path.IsPathRooted(full))
                    {
                        Tools.Logger("压缩包条目路径异常，已拒绝: " + full);
                        return null;
                    }
                    total += entry.Length;
                    if (total > MaxUncompressedBytes)
                    {
                        Tools.Logger("压缩包解压体积超出上限，已拒绝");
                        return null;
                    }
                    if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length > MaxExeBytes)
                    {
                        Tools.Logger("可执行文件体积超出上限，已拒绝");
                        return null;
                    }
                    using (var es = entry.Open())
                    using (var ems = new MemoryStream())
                    {
                        es.CopyTo(ems);
                        return ems.ToArray();
                    }
                }
            }
            Tools.Logger("压缩包中未找到可执行文件");
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        /// <summary>
        /// 用 Windows 原生 WinVerifyTrust 校验 Authenticode 签名与证书链。
        /// 离线环境不做吊销联机检查，但签名缺失或证书链不可信一律拒绝启用。
        /// </summary>
        private static bool VerifyAuthenticode(string path, out string detail)
        {
            detail = "";
            IntPtr pInfo = IntPtr.Zero;
            IntPtr pPath = IntPtr.Zero;
            try
            {
                pPath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
                var info = new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    pcwszFilePath = pPath
                };
                pInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(info, pInfo, false);

                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,           // WTD_UI_NONE：不弹任何界面
                    fdwRevocationChecks = 0,  // WTD_REVOKE_NONE：离线环境可校验
                    dwUnionChoice = 1,        // WTD_CHOICE_FILE
                    pFile = pInfo,
                    dwStateAction = 1         // WTD_STATEACTION_VERIFY
                };
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                var hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                data.dwStateAction = 2;       // WTD_STATEACTION_CLOSE：释放状态句柄
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                if (hr != 0)
                {
                    detail = "Authenticode 校验未通过 (0x" + hr.ToString("X8") + ")";
                    return false;
                }
                detail = "Authenticode 校验通过";
                return true;
            }
            catch (Exception ex)
            {
                detail = "签名校验失败: " + ex.Message;
                return false;
            }
            finally
            {
                if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo);
                if (pPath != IntPtr.Zero) Marshal.FreeHGlobal(pPath);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}

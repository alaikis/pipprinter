using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>Gitee 自动更新清单条目（latest.json）。</summary>
    public class UpdateManifest
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }
        public string PublishedAt { get; set; }
    }

    /// <summary>
    /// 自动更新服务：从 Gitee Release 拉取 latest.json，校验 SHA256，下载单文件包，替换当前目录，重启。
    /// 单文件包（PipPrinter.exe）由 Gitee Go 流水线构建，与本地构建产物一致。
    /// </summary>
    public sealed class UpdateService
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        private readonly string _downloadDir;
        private readonly string _installDir;
        private readonly string _currentExe;

        public UpdateService()
        {
            _installDir = AppDomain.CurrentDomain.BaseDirectory;
            _downloadDir = Path.Combine(SettingsStore.Instance.TempDir, "update");
            _currentExe = Path.Combine(_installDir, "PipPrinter.exe");
            if (!Directory.Exists(_downloadDir)) Directory.CreateDirectory(_downloadDir);
        }

        /// <summary>拉取清单 + 比对当前版本，返回 UpdateInfo。不实际下载。</summary>
        public async Task<UpdateInfo> CheckUpdateAsync()
        {
            var s = SettingsStore.Instance.Current;
            if (string.IsNullOrWhiteSpace(s.UpdateManifestUrl))
                return new UpdateInfo { HasUpdate = false, Error = "未配置 UpdateManifestUrl" };
            string manifestUrl = s.UpdateManifestUrl;
            // 若配置的是 release 页地址，拼出 latest.json
            if (manifestUrl.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase) ||
                manifestUrl.Contains("/releases/"))
            {
                manifestUrl = manifestUrl + "/download/latest.json";
            }
            try
            {
                Http.DefaultRequestHeaders.Accept.Clear();
                Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var json = await Http.GetStringAsync(manifestUrl).ConfigureAwait(false);
                var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                    return new UpdateInfo
                    {
                        HasUpdate = false,
                        Current = Brand.Version,
                        Error = "manifest 缺少 version 字段（Release 尚未发布或地址无效）",
                        Url = manifestUrl
                    };
                var current = Brand.Version;
                var newer = IsNewer(manifest.Version, current);
                return new UpdateInfo
                {
                    HasUpdate = newer,
                    Current = current,
                    Remote = manifest.Version,
                    Url = manifest.Url,
                    Sha256 = manifest.Sha256,
                    Size = manifest.Size,
                    Notes = manifest.Notes,
                    PublishedAt = manifest.PublishedAt,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("CheckUpdate 失败: " + ex.Message);
                return new UpdateInfo { HasUpdate = false, Error = ex.Message, Url = manifestUrl };
            }
        }

        /// <summary>
        /// 执行更新：下载 → 校验 SHA256 → 备份旧目录 → 替换 → 返回新 exe 路径（由调用方决定是否重启）。
        /// </summary>
        public async Task<UpdateInfo> ApplyUpdateAsync()
        {
            var check = await CheckUpdateAsync().ConfigureAwait(false);
            if (!check.HasUpdate)
            {
                check.Error = "没有可用更新";
                return check;
            }
            try
            {
                var targetPath = Path.Combine(_downloadDir, "PipPrinter-" + check.Remote + ".exe");
                using (var resp = await Http.GetAsync(check.Url,
                    HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using (var file = File.Create(targetPath))
                    {
                        await stream.CopyToAsync(file).ConfigureAwait(false);
                    }
                }
                var hash = await Sha256Of(targetPath).ConfigureAwait(false);
                if (check.Sha256 != null &&
                    !string.Equals(hash, check.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateInfo { HasUpdate = false, Error = "SHA256 校验失败" };
                }
                // 备份旧目录
                var backup = _installDir + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                if (Directory.Exists(_installDir))
                {
                    Directory.Move(_installDir, backup);
                }
                Directory.CreateDirectory(_installDir);
                // 拷贝新 exe + 资源
                File.Copy(targetPath, Path.Combine(_installDir, "PipPrinter.exe"));
                // 把旧目录里的 config/docs 拷回
                foreach (var sub in new[] { "config", "docs", "pdfreader", "Resource" })
                {
                    var src = Path.Combine(backup, sub);
                    var dst = Path.Combine(_installDir, sub);
                    if (Directory.Exists(src))
                    {
                        CopyDir(src, dst);
                    }
                }
                Tools.Logger("更新完成：旧版备份到 " + backup + "，新版安装到 " + _installDir);
                return new UpdateInfo
                {
                    HasUpdate = false,
                    Installed = true,
                    BackupPath = backup,
                    ExePath = Path.Combine(_installDir, "PipPrinter.exe"),
                    Error = "已替换；请重启应用以生效",
                    Current = check.Remote
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("ApplyUpdate 失败: " + ex);
                return new UpdateInfo { HasUpdate = false, Error = ex.Message };
            }
        }

        private static async Task<string> Sha256Of(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs).ConfigureAwait(false);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>语义化版本比较：newer > current 返回 true。任一侧为空时返回 false（宁可不更新）。</summary>
        public static bool IsNewer(string newer, string current)
        {
            if (string.IsNullOrWhiteSpace(newer) || string.IsNullOrWhiteSpace(current))
                return false;
            try
            {
                var a = Split(newer);
                var b = Split(current);
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int x = i < a.Length ? a[i] : 0;
                    int y = i < b.Length ? b[i] : 0;
                    if (x != y) return x > y;
                }
                return false;
            }
            catch
            {
                // 版本号无法解析时，按「不同即更新」策略
                return !string.Equals(newer, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static int[] Split(string v)
        {
            var raw = v.Trim().TrimStart('v');
            var parts = raw.Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n = 0;
                foreach (var c in parts[i])
                {
                    if (c >= '0' && c <= '9') n = n * 10 + (c - '0');
                    else break;
                }
                nums[i] = n;
            }
            return nums;
        }

        private static void CopyDir(string src, string dst)
        {
            if (Directory.Exists(dst)) return;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }
    }

    /// <summary>更新检查结果。</summary>
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public bool Installed { get; set; }
        public string Current { get; set; }
        public string Remote { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }
        public string PublishedAt { get; set; }
        public string Error { get; set; }
        public string BackupPath { get; set; }
        public string ExePath { get; set; }
    }
}

// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>自动更新清单条目（latest.json；主源 GitHub，Gitee 备份）。</summary>
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
    /// 自动更新服务：解析最新 Release（主源 GitHub / 备份 Gitee）→ 拉取 latest.json → 版本比对 →
    /// 下载 zip → SHA256 校验 → 旧 exe 改名让位、新 exe 就位（用户数据不动）→ 由调用方决定是否重启。
    /// 主源 GitHub：/releases/latest 的 assets 与 Gitee 同为 browser_download_url，取法一致；
    /// GitHub 无附件总量上限，作主仓库；Gitee 1GB 附件配额降为备份源。
    /// </summary>
    public sealed class UpdateService
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        private readonly string _installDir;
        // 下载暂存必须在安装目录之外：替换时会整体移动安装目录，内部的暂存文件路径会全部失效
        private readonly string _downloadDir;

        public UpdateService()
        {
            _installDir = AppDomain.CurrentDomain.BaseDirectory;
            _downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PipPrinter", "update");
            if (!Directory.Exists(_downloadDir)) Directory.CreateDirectory(_downloadDir);
        }

        /// <summary>
        /// 把配置的 UpdateManifestUrl 解析为 latest.json 的实际下载地址。
        /// - 以 .json 结尾：直接使用
        /// - https://github.com/{owner}/{repo}/releases（GitHub，主源，2026-11-24 起）：
        ///   调 /repos/{owner}/{repo}/releases/latest，取 assets 里 latest.json 的 browser_download_url
        /// - https://gitee.com/{owner}/{repo}/releases（Gitee，备份源）：
        ///   调 API v5 /repos/{owner}/{repo}/releases/latest，取 assets 里 latest.json 的 browser_download_url
        /// </summary>
        private static async Task<string> ResolveManifestUrlAsync(string configured, CancellationToken cancellationToken = default)
        {
            if (configured.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return configured;

            // 主源：GitHub（/releases/latest 的 assets 与 Gitee 同为 browser_download_url，取法一致）
            var gh = Regex.Match(configured, @"github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?/releases",
                RegexOptions.IgnoreCase);
            if (gh.Success)
                return await FetchLatestJsonUrlAsync(
                    string.Format("https://api.github.com/repos/{0}/{1}/releases/latest",
                        gh.Groups[1].Value, gh.Groups[2].Value), cancellationToken).ConfigureAwait(false);

            // 备份源：Gitee
            var gg = Regex.Match(configured, @"gitee\.com[:/]([^/]+)/([^/]+?)(?:\.git)?/releases",
                RegexOptions.IgnoreCase);
            if (gg.Success)
                return await FetchLatestJsonUrlAsync(
                    string.Format("https://gitee.com/api/v5/repos/{0}/{1}/releases/latest",
                        gg.Groups[1].Value, gg.Groups[2].Value), cancellationToken).ConfigureAwait(false);

            // 未知托管：退化为 <url>/download/latest.json
            return configured.TrimEnd('/') + "/download/latest.json";
        }

        /// <summary>调 latest-release API，取 assets 里名为 latest.json 的附件直链。</summary>
        private static async Task<string> FetchLatestJsonUrlAsync(string api, CancellationToken cancellationToken)
        {
            var json = await Http.GetStringAsync(api, cancellationToken).ConfigureAwait(false);
            var rel = JObject.Parse(json);
            var asset = (rel["assets"] as JArray)?
                .FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "latest.json",
                    StringComparison.OrdinalIgnoreCase));
            var url = asset?["browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("最新 Release 不含 latest.json 附件");
            return url;
        }

        /// <summary>拉取清单 + 比对当前版本，返回 UpdateInfo。不实际下载。可传 token 限制后台检查耗时。</summary>
        public async Task<UpdateInfo> CheckUpdateAsync(CancellationToken cancellationToken = default)
        {
            var s = SettingsStore.Instance.Current;
            if (string.IsNullOrWhiteSpace(s.UpdateManifestUrl))
                return new UpdateInfo { HasUpdate = false, Error = "未配置 UpdateManifestUrl" };
            string manifestUrl = null;
            try
            {
                Http.DefaultRequestHeaders.Accept.Clear();
                Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                manifestUrl = await ResolveManifestUrlAsync(s.UpdateManifestUrl, cancellationToken).ConfigureAwait(false);
                var json = await Http.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
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
                var remote = manifest.Version.Trim().TrimStart('v');
                var newer = IsNewer(remote, current);
                return new UpdateInfo
                {
                    HasUpdate = newer,
                    Current = current,
                    Remote = remote,
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
                return new UpdateInfo
                {
                    HasUpdate = false,
                    Error = ex.Message,
                    Url = manifestUrl ?? s.UpdateManifestUrl,
                    NetworkError = IsNetworkFailure(ex)
                };
            }
        }

        /// <summary>
        /// 区分「网络/主机不可达」与「清单内容错误」，仅供自动检查退避使用。
        /// 4xx（清单地址错、资源不存在）重试无益，不算网络故障；5xx/无状态码按暂时性故障处理。
        /// </summary>
        private static bool IsNetworkFailure(Exception ex)
        {
            if (ex == null) return false;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                    if (IsNetworkFailure(inner)) return true;
                return false;
            }
            if (ex is OperationCanceledException) return true; // 含后台检查超时
            if (ex is System.Net.Sockets.SocketException) return true;
            if (ex is System.Net.WebException) return true;
            if (ex is HttpRequestException hre)
            {
                if (hre.StatusCode.HasValue)
                {
                    var code = (int)hre.StatusCode.Value;
                    return code == 408 || code == 429 || code >= 500;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 执行更新：下载 zip → 校验 SHA256 → 解包暂存 →
        /// 旧 exe 改名让位（Windows 允许改名运行中的 exe）→ 新 exe 就位 + 覆盖随包资源 →
        /// config/data/log 用户数据不动；失败自动回滚。重启后由 CleanupLeftovers 清理残留。
        /// </summary>
        public Task<UpdateInfo> ApplyUpdateAsync() => ApplyUpdateAsync(silent: false);

        /// <summary>
        /// 执行更新；silent=true 时替换成功后【不立即重启】，把 pendingRestart 写入 config，
        /// 由启动时的「延迟重启」定时器在用户空闲 + 打印队列空闲时生效。
        /// 用于闲时静默升级：后台自动下载替换、不打断用户，避免在打印任务进行中硬重启。
        /// </summary>
        public async Task<UpdateInfo> ApplyUpdateAsync(bool silent)
        {
            var check = await CheckUpdateAsync().ConfigureAwait(false);
            if (!check.HasUpdate)
            {
                check.Error = check.Error ?? "没有可用更新";
                return check;
            }
            string oldExePath = null;
            try
            {
                // 1. 下载 zip 到安装目录外的暂存区
                var zipPath = Path.Combine(_downloadDir,
                    "PipPrinter-" + check.Remote + "-" + Path.GetFileName(new Uri(check.Url).AbsolutePath));
                using (var resp = await Http.GetAsync(check.Url,
                    HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using (var file = File.Create(zipPath))
                    {
                        await stream.CopyToAsync(file).ConfigureAwait(false);
                    }
                }

                // 2. SHA256 校验
                var hash = await Sha256Of(zipPath).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(check.Sha256) &&
                    !string.Equals(hash, check.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo { HasUpdate = false, Error = "SHA256 校验失败", Url = check.Url };

                // 3. 解出发布目录到暂存区
                var extractDir = Path.Combine(_downloadDir, "extract-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                var newExe = FindFile(extractDir, "PipPrinter.exe");
                if (newExe == null)
                    return new UpdateInfo { HasUpdate = false, Error = "zip 内未找到 PipPrinter.exe" };

                // 4. 就地替换：Windows 不允许删除/移动运行中的 exe，但允许改名。
                //    旧 exe 改名让位（重启后由 CleanupLeftovers 清理），新 exe 拷入。
                //    config/（用户设置+machineId）、data/、log/ 完全不动。
                var exePath = Path.Combine(_installDir, "PipPrinter.exe");
                oldExePath = exePath + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(exePath, oldExePath);

                try
                {
                    // 5. 新 exe 就位
                    File.Copy(newExe, exePath, true);

                    // 6. 覆盖随包资源（docs/web 文档、pdfreader 驱动、图标）
                    foreach (var sub in new[] { "docs", "pdfreader", "Resource" })
                    {
                        var src = Path.Combine(extractDir, sub);
                        if (Directory.Exists(src)) CopyDir(src, Path.Combine(_installDir, sub));
                    }
                }
                catch
                {
                    // 7. 回滚：删掉未就位的新 exe，旧 exe 改回原名
                    try
                    {
                        if (File.Exists(exePath)) File.Delete(exePath);
                        if (File.Exists(oldExePath)) File.Move(oldExePath, exePath);
                        oldExePath = null;
                    }
                    catch (Exception rex) { Tools.Logger("更新回滚失败: " + rex.Message); }
                    throw;
                }

                Tools.Logger("更新完成 v" + check.Remote + "：旧 exe 备份为 " + Path.GetFileName(oldExePath) +
                             (silent ? "，静默替换不立即重启，待下次启动时延迟重启" : "，重启后生效"));
                if (silent)
                {
                    SettingsStore.Instance.Update(s => s.PendingRestart = true);
                }
                return new UpdateInfo
                {
                    HasUpdate = false,
                    Installed = true,
                    BackupPath = oldExePath,
                    ExePath = exePath,
                    RestartPending = silent,
                    Error = silent ? "已静默替换，将在用户空闲且打印队列空闲时自动重启生效" : "已替换；请重启应用以生效",
                    Current = check.Remote
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("ApplyUpdate 失败: " + ex);
                return new UpdateInfo { HasUpdate = false, Error = ex.Message, BackupPath = oldExePath };
            }
        }

        /// <summary>
        /// 启动时清理上次更新留下的 PipPrinter.exe.old-*（此时旧进程已退出，文件不再被锁）。
        /// 在 Program 启动早期调用。
        /// </summary>
        public static void CleanupLeftovers()
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var f in Directory.GetFiles(dir, "PipPrinter.exe.old-*"))
                {
                    try { File.Delete(f); Tools.Logger("已清理更新残留: " + Path.GetFileName(f)); }
                    catch { /* 仍被占用则下次再清 */ }
                }
            }
            catch { /* 忽略 */ }
        }

        private static string FindFile(string dir, string fileName)
        {
            var hit = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            return hit;
        }

        private static async Task<string> Sha256Of(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs).ConfigureAwait(false);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>语义化版本比较：newer &gt; current 返回 true。任一侧为空时返回 false（宁可不更新）。</summary>
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
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
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
        /// <summary>静默升级：exe 已替换就位但尚未重启；下次启动时由「延迟重启」定时器生效。</summary>
        public bool RestartPending { get; set; }
        /// <summary>网络/主机不可达标记，仅用于自动检查退避；不进入 REST 响应。</summary>
        [JsonIgnore]
        public bool NetworkError { get; set; }
    }
}

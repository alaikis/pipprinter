using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using DuoAssistant.Models;
using DuoAssistant.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 所有对外命令的实现。每个公开实例方法必须签名 (string args) → string，
    /// 供 CommandDispatcher 通过反射分派（WebSocket / REST / UI 共用）。
    /// 返回值统一为 Result 结构的 JSON 字符串。
    /// </summary>
    public class PrintServices
    {
        private readonly ExtendedPrintService _extended = new ExtendedPrintService();
        private readonly PrinterService _printers = new PrinterService();
        private readonly DiskCleanerService _cleaner = new DiskCleanerService();

        // ---------------------------------------------------------------- 打印

        /// <summary>
        /// 原有 PDF 网络打印命令（保持兼容）。现在复用多类型打印引擎。
        /// 支持 label 为 http(s) / 协议相对 URL / 本地路径。
        /// </summary>
        public string PrintPdf(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var label = Str(dt, "label") ?? Str(dt, "url") ?? Str(dt, "path");
                if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("缺少 label 字段");

                var job = BuildJob(dt, label);
                var outcome = _extended.Print(job);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "Print", data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    label,
                    printer = job.Printer,
                    engine = outcome.Engine,
                    message = outcome.Message
                }, cmd: "Print", msg: "print successful!");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "Print");
            }
        }

        /// <summary>打印任意受支持类型的文件 / URL（同步）。</summary>
        public string PrintFile(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var target = Str(dt, "path") ?? Str(dt, "label") ?? Str(dt, "url") ?? Str(dt, "file");
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 path 字段");
                var job = BuildJob(dt, target);
                var outcome = _extended.Print(job);
                if (!outcome.Ok)
                    return Result.error(outcome.Message, cmd: "PrintFile", data: new { detail = outcome.Detail });
                return Result.success(data: new
                {
                    path = target, printer = job.Printer, engine = outcome.Engine,
                    message = outcome.Message, supportedTypes = PrintFileTypes.All
                }, cmd: "PrintFile", msg: outcome.Message);
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "PrintFile");
            }
        }

        /// <summary>
        /// 模板打印：template 为内联文本或模板文件路径，data 为 {{key}} 占位符值。
        /// 渲染后入队打印。
        /// </summary>
        public string TemplatePrint(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var template = Str(dt, "template");
                if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("缺少 template 字段");

                string rendered;
                string path;
                var isHtml = template.IndexOf("<", StringComparison.Ordinal) >= 0 &&
                             template.IndexOf(">", StringComparison.Ordinal) >= 0;

                if (File.Exists(template))
                {
                    path = template;
                    rendered = File.ReadAllText(path);
                    isHtml = Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    rendered = template;
                    path = Path.Combine(SettingsStore.Instance.TempDir,
                        Guid.NewGuid().ToString("N") + (isHtml ? ".html" : ".txt"));
                }

                if (dt["data"] is JObject data)
                {
                    foreach (var kv in data.Properties())
                        rendered = rendered.Replace("{{" + kv.Name + "}}", kv.Value.ToString());
                }
                if (!File.Exists(path)) File.WriteAllText(path, rendered);

                var job = BuildJob(dt, path);
                var queued = PrintQueueService.Instance.Enqueue(job);
                return Result.success(data: new { jobId = queued.Id, renderedPath = path, isHtml },
                    cmd: "TemplatePrint", msg: "模板已渲染并入队");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "TemplatePrint");
            }
        }

        // ---------------------------------------------------------------- 队列

        public string QueuePrint(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var target = Str(dt, "path") ?? Str(dt, "label") ?? Str(dt, "url") ?? Str(dt, "file");
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 path 字段");
                var job = BuildJob(dt, target);
                var queued = PrintQueueService.Instance.Enqueue(job);
                return Result.success(data: new { jobId = queued.Id, job = queued },
                    cmd: "QueuePrint", msg: "已加入打印队列");
            }
            catch (Exception ex)
            {
                return Result.error(ex.Message, cmd: "QueuePrint");
            }
        }

        public string GetQueue(string args)
        {
            var queue = PrintQueueService.Instance;
            return Result.success(data: new { jobs = queue.GetAll(), summary = queue.Summary() },
                cmd: "GetQueue");
        }

        public string RetryJob(string args)
        {
            var id = Str(ParseObject(args), "id") ?? Str(ParseObject(args), "jobId");
            if (string.IsNullOrWhiteSpace(id)) return Result.error("缺少 id 字段", cmd: "RetryJob");
            var ok = PrintQueueService.Instance.Retry(id);
            return ok
                ? Result.success(data: new { id }, cmd: "RetryJob", msg: "任务已重新入队")
                : Result.error("任务不存在", 404, "RetryJob");
        }

        public string CancelJob(string args)
        {
            var id = Str(ParseObject(args), "id");
            if (string.IsNullOrWhiteSpace(id)) return Result.error("缺少 id 字段", cmd: "CancelJob");
            var ok = PrintQueueService.Instance.Cancel(id);
            return ok
                ? Result.success(data: new { id }, cmd: "CancelJob", msg: "任务已取消")
                : Result.error("任务不存在或正在打印", 400, "CancelJob");
        }

        public string ClearQueue(string args)
        {
            var dt = ParseObject(args);
            var finishedOnly = Bool(dt, "finishedOnly", true);
            var removed = PrintQueueService.Instance.Clear(finishedOnly);
            return Result.success(data: new { removed, finishedOnly }, cmd: "ClearQueue");
        }

        // ------------------------------------------------------------ 打印机

        /// <summary>保留旧结构（printers: string[]、primary），并附 detail。</summary>
        public string GetPrinterList(string args)
        {
            var names = _printers.GetNames();
            return Result.success(data: new
            {
                printers = names,
                primary = _printers.GetDefault(),
                detail = _printers.GetInstalled()
            }, cmd: "GetPrinterList");
        }

        public string GetPrinterDetail(string args) =>
            Result.success(data: _printers.GetInstalled(), cmd: "GetPrinterDetail");

        public string SetDefaultPrinter(string args)
        {
            var name = Str(ParseObject(args), "printer") ?? Str(ParseObject(args), "name");
            if (string.IsNullOrWhiteSpace(name)) return Result.error("缺少 printer 字段", cmd: "SetDefaultPrinter");
            var ok = _printers.SetDefault(name);
            return ok
                ? Result.success(data: new { primary = name }, cmd: "SetDefaultPrinter", msg: "默认打印机已更新")
                : Result.error("打印机不存在或切换失败", 400, "SetDefaultPrinter");
        }

        public string PrintTestPage(string args)
        {
            var name = Str(ParseObject(args), "printer");
            var ok = _printers.PrintTestPage(name);
            return ok
                ? Result.success(data: new { printer = name ?? _printers.GetDefault() },
                    cmd: "PrintTestPage", msg: "测试页已发送")
                : Result.error("测试页打印失败，请检查打印机是否可用", cmd: "PrintTestPage");
        }

        // ------------------------------------------------------ PDF 驱动配置

        public string GetPdfDriverConfig(string args)
        {
            var targetFile = Path.Combine(SettingsStore.Instance.DriverDir, "SumatraPDF-settings.txt");
            var config = "";
            try
            {
                if (File.Exists(targetFile)) config = File.ReadAllText(targetFile);
            }
            catch (Exception ex) { Tools.Logger("读取驱动配置失败: " + ex.Message); }
            return Result.success(data: new { config, path = targetFile }, cmd: "GetPdfDriverConfig");
        }

        public string SetPdfPrintDriverConfig(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var config = Str(dt, "config");
                if (config == null) return Result.error("缺少 config 字段", cmd: "SetPdfPrintDriverConfig");
                var targetFile = Path.Combine(SettingsStore.Instance.DriverDir, "SumatraPDF-settings.txt");
                File.WriteAllText(targetFile, config);
                return Result.success(data: new { path = targetFile },
                    cmd: "SetPdfPrintDriverConfig", msg: "the config has updated");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "SetPdfPrintDriverConfig"); }
        }

        // ------------------------------------------------------------ 磁盘

        public string ScanDisk(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var age = Int(dt, "maxAgeDays", SettingsStore.Instance.Current.CleanMaxAgeDays);
                var minSize = Int(dt, "minSizeMB", SettingsStore.Instance.Current.CleanMinSizeMB);
                var items = _cleaner.Scan(age, minSize);
                return Result.success(data: new
                {
                    count = items.Count,
                    totalBytes = items.Sum(x => x.SizeBytes),
                    totalText = CleanCandidate.FormatSize(items.Sum(x => x.SizeBytes)),
                    items = items.Take(500).ToList(),
                    truncated = items.Count > 500
                }, cmd: "ScanDisk");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "ScanDisk"); }
        }

        /// <summary>
        /// 清理磁盘。默认 dryRun=true，必须显式传 dryRun:false 才会真正删除；
        /// 且只能删除 AllowedRoots 下、不在白名单内的文件。
        /// </summary>
        public string CleanDisk(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var paths = new List<string>();
                if (dt["paths"] is JArray arr)
                    paths.AddRange(arr.Select(x => x.ToString()));
                else
                {
                    var single = Str(dt, "path");
                    if (!string.IsNullOrWhiteSpace(single)) paths.Add(single);
                }
                var dryRun = Bool(dt, "dryRun", true);
                var recycle = Bool(dt, "useRecycleBin", SettingsStore.Instance.Current.UseRecycleBin);
                var result = _cleaner.Clean(paths, dryRun, recycle);
                return Result.success(data: result, cmd: "CleanDisk",
                    msg: dryRun ? "试运行完成，未删除任何文件" : "清理完成");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "CleanDisk"); }
        }

        public string GetDiskInfo(string args) =>
            Result.success(data: _cleaner.GetDiskInfo(), cmd: "GetDiskInfo");

        // ------------------------------------------------------------ 配置/状态

        public string GetSettings(string args) =>
            Result.success(data: SettingsStore.Instance.Current, cmd: "GetSettings");

        public string SaveSettings(string args)
        {
            try
            {
                var dt = ParseObject(args);
                var next = JsonConvert.DeserializeObject<AppSettings>(
                    JsonConvert.SerializeObject(SettingsStore.Instance.Current));
                foreach (var prop in dt.Properties())
                {
                    var pi = typeof(AppSettings).GetProperty(prop.Name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null || !pi.CanWrite) continue;
                    var value = prop.Value.ToObject(pi.PropertyType);
                    pi.SetValue(next, value);
                }
                SettingsStore.Instance.Save(next);
                return Result.success(data: next, cmd: "SaveSettings",
                    msg: "配置已保存；端口/自启等改动需重启应用生效");
            }
            catch (Exception ex) { return Result.error(ex.Message, cmd: "SaveSettings"); }
        }

        public string GetStatus(string args)
        {
            var s = SettingsStore.Instance.Current;
            return Result.success(data: new
            {
                app = Brand.NameEn,
                appCn = Brand.NameCn,
                version = Brand.Version,
                framework = Environment.Version.ToString(),
                websocket = "ws://" + s.WsHost + ":" + s.WsPort,
                webDocs = "http://" + s.WebHost + ":" + s.WebPort,
                defaultPrinter = _printers.GetDefault(),
                supportedTypes = PrintFileTypes.All,
                queue = PrintQueueService.Instance.Summary(),
                uptimeSeconds = (long)(DateTime.Now - PrintQueueService.Instance.StartedAt).TotalSeconds
            }, cmd: "GetStatus");
        }

        /// <summary>本机指纹：跨浏览器/会话稳定，作为打印习惯的 machine-key。</summary>
        public string GetMachineFingerprint(string args)
        {
            var fp = Utils.MachineFingerprint.Get();
            return Result.success(data: new
            {
                machineId = fp,
                machineGuid = Utils.MachineFingerprint.MachineGuid(),
                volumeSerial = Utils.MachineFingerprint.VolumeSerial(),
                os = Environment.OSVersion.VersionString,
                user = Environment.UserName,
                app = Brand.NameEn,
                appCn = Brand.NameCn,
                version = Utils.Brand.Version
            }, cmd: "GetMachineFingerprint");
        }

        // ------------------------------------------------------------ 更新

        /// <summary>拉取 Gitee latest.json，比对当前版本，不实际下载。</summary>
        public string CheckUpdate(string args)
        {
            var info = new UpdateService().CheckUpdateAsync().GetAwaiter().GetResult();
            return Result.success(data: info, cmd: "CheckUpdate",
                msg: info.HasUpdate ? "发现新版本 " + info.Remote : "已是最新版本");
        }

        /// <summary>下载、校验 SHA256、替换当前目录。返回 Installed 标识与备份路径。</summary>
        public string ApplyUpdate(string args)
        {
            var info = new UpdateService().ApplyUpdateAsync().GetAwaiter().GetResult();
            return info.Installed
                ? Result.success(data: info, cmd: "ApplyUpdate", msg: "已更新；请重启应用")
                : Result.error(info.Error ?? "更新失败", 500, "ApplyUpdate", data: info);
        }

        // ------------------------------------------------------------ 辅助

        private static JObject ParseObject(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return new JObject();
            var token = JToken.Parse(args);
            if (token is JObject o) return o;
            throw new ArgumentException("请求体必须是 JSON 对象");
        }

        private static string Str(JObject o, string key)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? null : v.ToString();
        }

        private static int Int(JObject o, string key, int fallback)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? fallback : v.Value<int>();
        }

        private static bool Bool(JObject o, string key, bool fallback)
        {
            var v = o?[key];
            return v == null || v.Type == JTokenType.Null ? fallback : v.Value<bool>();
        }

        private static PrintJob BuildJob(JObject dt, string target)
        {
            var isHttp = ExtendedPrintService.NormalizeUrlSafe(target) != null;
            return new PrintJob
            {
                Source = isHttp ? "url" : "file",
                Target = target,
                Printer = Str(dt, "printer") ?? SettingsStore.Instance.Current.DefaultPrinter ?? "",
                Copies = Math.Max(1, Int(dt, "copies", 1)),
                Fit = Str(dt, "fit") ?? "fit",
                Scale = Str(dt, "scale") ?? "portrait",
                PaperKind = Str(dt, "paperKind") ?? "",
                Pages = Str(dt, "printPages") ?? Str(dt, "pages") ?? "1-"
            };
        }
    }
}

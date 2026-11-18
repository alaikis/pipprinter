// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 应用配置模型。字段与 config/appsettings.json 一一对应。
    /// </summary>
    public class AppSettings
    {
        // 服务监听主机/端口：HTTP 文档/REST 与 WebSocket（/ws）共用同一端口。
        // 原 WsHost/WsPort 已弃用并移除（旧配置文件中的这两个字段会被忽略）。
        public string WebHost { get; set; } = "0.0.0.0";
        public int WebPort { get; set; } = 19861;
        public string DefaultPrinter { get; set; } = "";
        // ---- 默认打印参数：API 未显式传对应字段时回退到这些值（设置页「默认打印参数」可改）----
        public int DefaultCopies { get; set; } = 1;
        public string DefaultOrientation { get; set; } = "portrait";
        public string DefaultPaperSize { get; set; } = "";
        public string DefaultDuplex { get; set; } = "simplex";
        public int DefaultMargin { get; set; } = 50;
        public string DefaultPageOrder { get; set; } = "";
        public bool AutoStart { get; set; } = true;
        public string LogLevel { get; set; } = "DEBUG";
        public int CleanMaxAgeDays { get; set; } = 7;
        public int CleanMinSizeMB { get; set; } = 1;
        public bool UseRecycleBin { get; set; } = true;
        // 启动时自动清理应用临时文件（temp/）：删除超过 N 天的文件；0=关闭。
        // 只作用于应用自产临时文件（条码/ESC-POS dump/转换中间件等），不碰系统 Temp。
        public int TempAutoCleanMaxAgeDays { get; set; } = 7;
        public List<string> AllowedCommands { get; set; } = new List<string>();
        public List<string> CleanWhitelist { get; set; } = new List<string>();
        // 本机指纹（MachineFingerprint.Get() 生成），跨浏览器/会话稳定，作为打印习惯的 machine-key
        public string MachineId { get; set; } = "";
        // Gitee 自动更新清单 URL（latest.json 所在 release 的 attach_files 直链）
        public string UpdateManifestUrl { get; set; } = "https://gitee.com/alaikiscom/pipprinter/releases/latest";
        // ---- 自动检查更新（启动延迟检查一次 + 后台按间隔定时检查；只提示，不自动下载）----
        // 是否启用自动检查；默认开启。
        public bool AutoCheckUpdate { get; set; } = true;
        // 后台检查间隔（小时）；0 或负数会被 Load 补为默认值 6。
        public int AutoCheckIntervalHours { get; set; } = 6;
        // 界面语言：auto（跟随系统）/ zh-CN / en；未匹配的系统语言回退英文
        public string Language { get; set; } = "auto";
        // ---- Active-Online Print（AO 主动在线打印）----
        // 启用后本终端主动连接中继服务器（AoRelay），接受广域网远程打印任务
        public bool AoEnabled { get; set; } = false;
        // 中继地址：ws(s)://host:port 或 http(s)://host:port（自动换算为 ws）
        public string AoRelayUrl { get; set; } = "ws://127.0.0.1:8081";
        // 中继共享密钥（AoRelay --key），留空表示不校验
        public string AoKey { get; set; } = "";
        // 终端显示名（中继设备列表展示用），留空则用机器名
        public string AoDeviceName { get; set; } = "";
        // OFD 转换器可执行文件路径（国产版式文档转 PDF；留空则用系统关联程序）
        public string OfdConverter { get; set; } = "";
    }

    /// <summary>
    /// 配置读写单例。所有路径都基于应用基目录，避免依赖当前工作目录。
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly Lazy<SettingsStore> Lazy =
            new Lazy<SettingsStore>(() => new SettingsStore(), true);

        private readonly object _gate = new object();
        private readonly string _configPath;

        public static SettingsStore Instance => Lazy.Value;

        public AppSettings Current { get; private set; }

        public string BaseDir { get; }
        public string ConfigDir => Path.Combine(BaseDir, "config");
        public string ConfigPath => _configPath;
        public string DataDir => Path.Combine(BaseDir, "data");
        public string TempDir => Path.Combine(BaseDir, "temp");
        public string LogDir => Path.Combine(BaseDir, "log");
        public string DriverDir => Path.Combine(BaseDir, "lib", "driver");
        /// <summary>内置 Office 转换器（LibreOffice 便携版）目录，soffice.exe 在此。</summary>
        public string OfficeConverterDir => Path.Combine(BaseDir, "lib", "office");
        public string DocsDir => Path.Combine(BaseDir, "docs", "web");
        public string QueuePath => Path.Combine(DataDir, "queue.json");

        private SettingsStore()
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(ConfigDir, "appsettings.json");
            EnsureDirectories();
            Current = Load();
        }

        private void EnsureDirectories()
        {
            foreach (var dir in new[] { ConfigDir, DataDir, TempDir, LogDir, DriverDir, OfficeConverterDir })
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
        }

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var text = File.ReadAllText(_configPath);
                    var loaded = JsonConvert.DeserializeObject<AppSettings>(text);
                    if (loaded != null)
                    {
                        // 缺字段时补默认值，保证旧配置升级不炸
                        if (loaded.WebPort <= 0) loaded.WebPort = 19861;
                        if (string.IsNullOrWhiteSpace(loaded.WebHost)) loaded.WebHost = "0.0.0.0";
                        if (loaded.CleanMaxAgeDays < 0) loaded.CleanMaxAgeDays = 7;
                        if (loaded.CleanMinSizeMB < 0) loaded.CleanMinSizeMB = 1;
                        if (loaded.AllowedCommands == null || loaded.AllowedCommands.Count == 0)
                            loaded.AllowedCommands = Defaults().AllowedCommands;
                        else
                        {
                            // 升级兼容：只补齐官方新增的只读 / 默认命令，用户已有配置其余项保持不变。
                            foreach (var added in new[] { "AutoPrint", "CheckPrinterDriver",
                                                          "MatchPrinter", "SetPrinterPreference", "GetPrinterPreferences" })
                                if (!loaded.AllowedCommands.Exists(x =>
                                        string.Equals(x, added, StringComparison.OrdinalIgnoreCase)))
                                    loaded.AllowedCommands.Add(added);
                        }
                        if (loaded.CleanWhitelist == null) loaded.CleanWhitelist = Defaults().CleanWhitelist;
                        if (string.IsNullOrWhiteSpace(loaded.Language)) loaded.Language = "auto";
                        // 默认打印参数：旧配置缺字段时补默认值（区分「未配置」与「显式设为 0/空」）
                        if (loaded.DefaultCopies <= 0) loaded.DefaultCopies = 1;
                        if (string.IsNullOrWhiteSpace(loaded.DefaultOrientation)) loaded.DefaultOrientation = "portrait";
                        // DefaultPaperSize / DefaultPageOrder / DefaultDuplex 允许空串（跟随打印机/系统默认），不强行补值
                        if (loaded.DefaultMargin < 0) loaded.DefaultMargin = 50;
                        // 自动检查更新：间隔 <=0 补默认（bool 缺字段时属性初始值 true 即默认开启，无需补）
                        if (loaded.AutoCheckIntervalHours <= 0) loaded.AutoCheckIntervalHours = 6;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                DuoAssistant.Utils.Tools.Logger("读取配置失败，使用默认值: " + ex.Message);
            }
            return Defaults();
        }

        private static AppSettings Defaults()
        {
            return new AppSettings
            {
                AllowedCommands = new List<string>
                {
                    "AutoPrint", "PrintPdf", "PrintFile", "PrintBarcode", "PrintToPdf", "PrintToXps", "PrintEscPos",
                    "TemplatePrint", "QueuePrint", "GetQueue",
                    "RetryJob", "CancelJob", "ClearQueue", "GetPrinterList", "GetPrinterDetail", "CheckPrinterDriver",
                    "SetDefaultPrinter", "PrintTestPage", "GetPdfDriverConfig", "SetPdfPrintDriverConfig",
                    "MatchPrinter", "SetPrinterPreference", "GetPrinterPreferences",
                    "ScanDisk", "CleanDisk", "CleanAppTemp", "GetDiskInfo", "GetSettings", "SaveSettings", "GetStatus",
                    "GetMachineFingerprint", "CheckUpdate", "ApplyUpdate"
                },
                CleanWhitelist = new List<string>
                {
                    "%WINDIR%", "%PROGRAMFILES%", "%PROGRAMFILES(X86)%",
                    "%USERPROFILE%\\Desktop", "%USERPROFILE%\\Documents",
                    "%USERPROFILE%\\Downloads", "%APPDATA%"
                }
            };
        }

        /// <summary>用一份完整配置覆盖并立即持久化。</summary>
        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (_gate)
            {
                Current = settings;
                var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
                AtomicWrite(_configPath, json);
            }
        }

        /// <summary>部分更新：只覆写调用方传入的字段。</summary>
        public void Update(Action<AppSettings> mutate)
        {
            lock (_gate)
            {
                mutate?.Invoke(Current);
                AtomicWrite(_configPath, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
        }

        public static void AtomicWrite(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}

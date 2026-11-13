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
        public string WsHost { get; set; } = "0.0.0.0";
        public int WsPort { get; set; } = 19860;
        public string WebHost { get; set; } = "127.0.0.1";
        public int WebPort { get; set; } = 19861;
        public string DefaultPrinter { get; set; } = "";
        public bool AutoStart { get; set; } = true;
        public string LogLevel { get; set; } = "DEBUG";
        public int CleanMaxAgeDays { get; set; } = 7;
        public int CleanMinSizeMB { get; set; } = 1;
        public bool UseRecycleBin { get; set; } = true;
        public List<string> AllowedCommands { get; set; } = new List<string>();
        public List<string> CleanWhitelist { get; set; } = new List<string>();
        // 本机指纹（MachineFingerprint.Get() 生成），跨浏览器/会话稳定，作为打印习惯的 machine-key
        public string MachineId { get; set; } = "";
        // Gitee 自动更新清单 URL（latest.json 所在 release 的 attach_files 直链）
        public string UpdateManifestUrl { get; set; } = "https://gitee.com/alaikiscom/pipprinter/releases/latest";
        // 界面语言：auto（跟随系统）/ zh-CN / en；未匹配的系统语言回退英文
        public string Language { get; set; } = "auto";
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
            foreach (var dir in new[] { ConfigDir, DataDir, TempDir, LogDir, DriverDir })
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
                        if (loaded.WsPort <= 0) loaded.WsPort = 19860;
                        if (loaded.WebPort <= 0) loaded.WebPort = 19861;
                        if (string.IsNullOrWhiteSpace(loaded.WsHost)) loaded.WsHost = "0.0.0.0";
                        if (string.IsNullOrWhiteSpace(loaded.WebHost)) loaded.WebHost = "127.0.0.1";
                        if (loaded.CleanMaxAgeDays < 0) loaded.CleanMaxAgeDays = 7;
                        if (loaded.CleanMinSizeMB < 0) loaded.CleanMinSizeMB = 1;
                        if (loaded.AllowedCommands == null || loaded.AllowedCommands.Count == 0)
                            loaded.AllowedCommands = Defaults().AllowedCommands;
                        if (loaded.CleanWhitelist == null) loaded.CleanWhitelist = Defaults().CleanWhitelist;
                        if (string.IsNullOrWhiteSpace(loaded.Language)) loaded.Language = "auto";
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
                    "PrintPdf", "PrintFile", "TemplatePrint", "QueuePrint", "GetQueue",
                    "RetryJob", "CancelJob", "ClearQueue", "GetPrinterList", "GetPrinterDetail",
                    "SetDefaultPrinter", "PrintTestPage", "GetPdfDriverConfig", "SetPdfPrintDriverConfig",
                    "ScanDisk", "CleanDisk", "GetDiskInfo", "GetSettings", "SaveSettings", "GetStatus",
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

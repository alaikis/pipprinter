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
        // ---- 闲时静默升级（AutoUpdateService 检测到新版时自动下载替换、不弹 UI 确认）----
        // 默认 false：保持现状（发现新版只弹托盘气泡，由用户点击才确认安装）。
        // 设 true 后：检测到新版 → 自动走 TriggerSilentUpdate → 下载/SHA256/替换（保留 config/data/log），
        // 但不立即重启，把 PendingRestart=true 写入 config；下次启动时由「延迟重启」定时器在
        // 用户空闲 + 打印队列空闲时自动重启，避免中断正在进行的打印任务。
        public bool AutoSilentUpdate { get; set; } = false;
        // 用户空闲判定阈值（分钟）：超过该时长无键鼠输入才视为「闲时」，允许静默下载/延迟重启。
        // 0 或负数 Load 时补 10；上限钳到 1440（24h）。
        public int SilentUpdateIdleMinutes { get; set; } = 10;
        // 静默升级已替换、待延迟重启标记。true 时启动定时器检测「用户空闲 + 打印队列空闲」后
        // 自动 Process.Start(新 exe) + Application.Exit；重启成功后 CleanupLeftovers 清旧 exe 并复位 false。
        public bool PendingRestart { get; set; } = false;
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
        // ---- 打印管理：费用核算与配额 ----
        // 黑白/彩色单页成本（用于费用流水与分摊；0 表示不计费）
        public double CostPerPageBW { get; set; } = 0;
        public double CostPerPageColor { get; set; } = 0;
        // 墨量告警阈值百分比（低于该值触发告警；0=关闭）
        public int TonerLowThreshold { get; set; } = 10;
        // 墨量 SNMP 轮询间隔（分钟）；0=关闭轮询
        public int TonerPollMinutes { get; set; } = 60;
        // 扫描收件目录监听：开启后自动打印 data/inbox 中的新文件
        public bool InboxWatcherEnabled { get; set; } = false;
        // ---- API 鉴权：留空=本机访问免鉴权（保持历史兼容）；设置后调用方需带 token ----
        // 支持请求头 X-Api-Token 或 Authorization: Bearer <token>。/health 与静态文档始终放行。
        public string ApiToken { get; set; } = "";
        // 非本机监听（WebHost=0.0.0.0 或具体网卡 IP）时是否强制要求 ApiToken；默认开启，防止局域网裸奔。
        public bool RequireTokenOnLan { get; set; } = true;
        // ---- 通知告警 ----
        public bool NotificationsEnabled { get; set; } = false;
        public string NotifyWebhookUrl { get; set; } = "";
        public string DingTalkWebhook { get; set; } = "";
        public string WeComWebhook { get; set; } = "";
        public string NotifyMailTo { get; set; } = "";
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; } = 25;
        public bool SmtpSsl { get; set; } = false;
        public string SmtpUser { get; set; } = "";
        public string SmtpPassword { get; set; } = "";
        public string SmtpFrom { get; set; } = "";
        // ---- 身份集成（LDAP/AD）----
        // LDAP 服务器地址；留空则用 Windows 本机登录身份兜底
        public string LdapServer { get; set; } = "";
        public int LdapPort { get; set; } = 389;
        public string LdapBaseDn { get; set; } = "";
        public string LdapUserAttribute { get; set; } = "sAMAccountName";
        // 用户名后缀（如 @corp.com），用户名未含 \ 或 @ 时自动追加
        public string LdapUserSuffix { get; set; } = "";
        public bool LdapUseSsl { get; set; } = false;
        // 是否允许调用方覆盖 userId（默认 false：绑定认证身份，杜绝伪造）
        public bool AllowUserIdOverride { get; set; } = false;
        // ---- 集中式多机后台 ----
        // 中心 API 根地址（如 http://192.168.1.10:19861）；留空=纯本机不联网
        public string CentralApiUrl { get; set; } = "";
        public string CentralApiToken { get; set; } = "";
        public int CentralSyncMinutes { get; set; } = 30;
        // ---- 定时扫描打卐机画像库、反向路由引擎〈无需印诡器详情〉——
        // 开启后每次跑一次局劑网扫描(WMI+SSDP+mDNS)+本机已装列表，归一化成画像囚存到库，后绪路由引擎读取，不需实时再扫。
        public bool AutoScanPrinters { get; set; } = false;
        // 定时扫描间隔(分钟)；0/负数 Load 补 5。
        public int ScanIntervalMinutes { get; set; } = 5;
        // 扫描方法位揧制(0x1=installed 0x2=ssdp 0x4=mdns 0x8=active)，默认全选。
        public int ScanMethods { get; set; } = 0xF;
        // 画像离线判定 TTL(秒)；超过该时长未再打到则标记离线。0/负数 Load 补 300。
        public int ProfileTtlSeconds { get; set; } = 300;
        // 反向路由引擎:printer 界札时按画像們掩选机（自动模式）。printer 非空则优先指定。
        public bool ReverseRoutingEnabled { get; set; } = true;
        // 自动模式选机策略: idle-first(充载最低，默认) / round-robin / nearest。
        public string ReverseRoutingPolicy { get; set; } = "idle-first";

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
                        // 存量/新装兼容：非本机监听(0.0.0.0 或网卡 IP) + requireTokenOnLan=true + apiToken 为空，
                        // 会命中 WebServerService 启动门禁拒绝监听，导致 /api/printers 等本机 API 全部不可用
                        // （安装器升级原样保留 config，故存量用户升级后同样中招；e2e 因测试副本改 127.0.0.1 掩盖了此问题）。
                        // 产品定位为本机服务，此处自动降级到回环地址，保证首次启动即可用；
                        // 需要局域网访问的用户请自行设置 apiToken（一旦配置则不会触发本迁移，行为不变）。
                        string _wh = (loaded.WebHost ?? "").Trim().ToLowerInvariant();
                        bool _loopback = _wh == "127.0.0.1" || _wh == "localhost" || _wh == "::1";
                        if (!_loopback && loaded.RequireTokenOnLan && string.IsNullOrWhiteSpace(loaded.ApiToken))
                        {
                            DuoAssistant.Utils.Tools.Logger("存量配置兼容：webHost=" + loaded.WebHost + " 未配 apiToken 会拒绝启动，自动降级为 127.0.0.1");
                            loaded.WebHost = "127.0.0.1";
                        }

                        if (loaded.CleanMaxAgeDays < 0) loaded.CleanMaxAgeDays = 7;
                        if (loaded.CleanMinSizeMB < 0) loaded.CleanMinSizeMB = 1;
                        if (loaded.AllowedCommands == null || loaded.AllowedCommands.Count == 0)
                            loaded.AllowedCommands = Defaults().AllowedCommands;
                        else
                        {
                            // 升级兼容：只补齐官方新增的只读 / 默认命令，用户已有配置其余项保持不变。
                            foreach (var added in new[] { "AutoPrint", "CheckPrinterDriver",
                                                          "MatchPrinter", "SetPrinterPreference", "GetPrinterPreferences",
                                                          "ScanLanPrinters", "PrintToIp",
                                                          "ListPrintSlugs", "AddPrinterSlug", "RemovePrinterSlug",
                                                          "ListPrintEvents", "ArchivePrintEvents",
                                                          "SchedulePrint", "ListSchedulePlans", "RemoveSchedulePlan",
                                                          "PrintBatch", "RecallJob", "RecommendPlan",
                                                          "ListHeldJobs", "ReleaseJob", "GetQuota", "SetQuota",
                                                          "GetCostReport", "GetPrintReport", "ListPolicies", "SetPolicy",
                                                          "RemovePolicy", "GetArchive", "CleanArchive",
                                                          "ListDriverPackages", "InstallPrinterDriver", "GetTonerLevels",
                                                          "PreviewJob", "ListInboxFiles", "PrintInboxFile",
                                                          "ListNotifications", "TestNotification",
                                                          "AuthenticateUser", "ListIdentityProviders",
                                                          "SyncCentral", "GetCentralStatus", "ListCentralClients", "TriggerSilentUpdate", "GetPrinterProfiles", "GetCentralPrinters" })
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
                        // 闲时静默升级：缺字段时 AutoSilentUpdate 默认 false（保持现状），
                        // SilentUpdateIdleMinutes 缺/负数补 10；上限钳 1440。
                        if (loaded.SilentUpdateIdleMinutes < 0) loaded.SilentUpdateIdleMinutes = 10;
                        if (loaded.SilentUpdateIdleMinutes == 0) loaded.SilentUpdateIdleMinutes = 10;
                        if (loaded.SilentUpdateIdleMinutes > 1440) loaded.SilentUpdateIdleMinutes = 1440;
                        // 定时扫描画像库：间隔 <=0 补 5；TTL <=0 补 300；Methods 默认全选。
                        if (loaded.ScanIntervalMinutes <= 0) loaded.ScanIntervalMinutes = 5;
                        if (loaded.ScanIntervalMinutes > 1440) loaded.ScanIntervalMinutes = 1440;
                        if (loaded.ProfileTtlSeconds <= 0) loaded.ProfileTtlSeconds = 300;
                        if (loaded.ScanMethods == 0) loaded.ScanMethods = 0xF;

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
                // 默认监听回环：本机服务开箱即用，不会因「非本机监听需 token」门禁拒绝启动。
                // 需要局域网访问的用户在 config/appsettings.json 配置 apiToken 后自行改回 0.0.0.0。
                WebHost = "127.0.0.1",
                AllowedCommands = new List<string>
                {
                    "AutoPrint", "PrintPdf", "PrintFile", "PrintBarcode", "PrintToPdf", "PrintToXps", "PrintEscPos",
                    "TemplatePrint", "QueuePrint", "GetQueue",
                    "RetryJob", "CancelJob", "ClearQueue", "GetPrinterList", "GetPrinterDetail", "CheckPrinterDriver",
                    "SetDefaultPrinter", "PrintTestPage", "GetPdfDriverConfig", "SetPdfPrintDriverConfig",
                    "MatchPrinter", "SetPrinterPreference", "GetPrinterPreferences",
                    "ScanDisk", "CleanDisk", "CleanAppTemp", "GetDiskInfo", "GetSettings", "SaveSettings", "GetStatus",
                    "GetMachineFingerprint", "CheckUpdate", "ApplyUpdate", "ScanLanPrinters", "PrintToIp",
                    "ListPrintSlugs", "AddPrinterSlug", "RemovePrinterSlug",
                    "ListPrintEvents", "ArchivePrintEvents",
                    "SchedulePrint", "ListSchedulePlans", "RemoveSchedulePlan",
                    "PrintBatch", "RecallJob", "RecommendPlan",
                    "ListHeldJobs", "ReleaseJob", "GetQuota", "SetQuota", "GetCostReport", "GetPrintReport",
                    "ListPolicies", "SetPolicy", "RemovePolicy", "GetArchive", "CleanArchive",
                    "ListDriverPackages", "InstallPrinterDriver", "GetTonerLevels", "PreviewJob",
                    "ListInboxFiles", "PrintInboxFile", "ListNotifications", "TestNotification",
                    "AuthenticateUser", "ListIdentityProviders",
                    "SyncCentral", "GetCentralStatus", "ListCentralClients", "TriggerSilentUpdate", "GetPrinterProfiles", "GetCentralPrinters"
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

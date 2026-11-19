// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 轻量多语言支持。
    /// 策略：以中文源串为 key（源语言零转换），英文词典全量翻译；
    /// 启动时按 config.language 解析——auto 时检测操作系统 UI 语言
    /// （zh* → 简体中文，其余 → 英文兜底），切换语言需重启应用。
    /// </summary>
    public static class L10n
    {
        public const string Auto = "auto";
        public const string ZhCn = "zh-CN";
        public const string En = "en";

        /// <summary>配置中的语言设置（auto / zh-CN / en）。</summary>
        public static string Configured { get; private set; } = Auto;

        /// <summary>当前生效语言。</summary>
        public static string Current { get; private set; } = ZhCn;

        /// <summary>操作系统检测出的语言。</summary>
        public static string Detected { get; private set; } = ZhCn;

        /// <summary>UI 语言切换是否需要重启（词典在构建 UI 时消费）。</summary>
        public static bool NeedsRestart => !string.Equals(Configured, ResolvedConfig(),
            StringComparison.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _en = new Dictionary<string, string>
        {
            // ---- 通用 / 窗口 ----
            ["提示"] = "Notice",
            ["确认"] = "Confirm",
            ["完成"] = "Done",
            ["失败"] = "Failed",
            ["失败："] = "Failed: ",
            ["设置"] = "Settings",
            ["就绪"] = "Ready",
            ["未运行"] = "not running",
            ["刷新"] = "Refresh",
            ["浏览…"] = "Browse…",
            ["打印机："] = "Printer:",
            ["端口："] = "Port:",
            ["日志级别："] = "Log level:",
            ["份数："] = "Copies:",
            ["缩放："] = "Fit:",
            ["方向："] = "Orientation:",
            ["纸张："] = "Paper:",
            ["页码："] = "Pages:",
            ["文件 / URL："] = "File / URL:",
            ["打印测试页"] = "Print test page",
            ["(默认)"] = "(default)",
            ["(无)"] = "(none)",
            ["(系统默认)"] = "(system default)",

            // ---- 标签页 ----
            ["打印"] = "Print",
            ["打印队列"] = "Print Queue",
            ["打印机"] = "Printers",
            ["磁盘清理"] = "Disk Cleanup",
            ["日志"] = "Logs",

            // ---- 托盘菜单 ----
            ["打开主窗口"] = "Open main window",
            ["快速打印…"] = "Quick print…",
            ["打印机列表"] = "Printer list",
            ["打开文档站点"] = "Open documentation",
            ["查看本机指纹"] = "View machine fingerprint",
            ["检查更新"] = "Check for updates",
            ["退出"] = "Exit",
            ["已启动，右键托盘图标可管理打印任务与文档服务。"] =
                "Started. Right-click the tray icon to manage print jobs and doc services.",
            ["已在系统托盘中运行。"] = " is already running in the system tray.",

            // ---- 状态栏 ----
            ["WS {0}:{1}/ws {2}   |   Web 文档 {3}   |   队列 等待 {4} / 打印中 {5} / 完成 {6} / 失败 {7}"] =
                "WS {0}:{1}/ws {2}   |   Web docs {3}   |   Queue: pending {4} / printing {5} / done {6} / failed {7}",
            ["  [队列待处理]"] = "  [queue pending]",

            // ---- 打印页 ----
            ["立即打印"] = "Print now",
            ["加入队列"] = "Add to queue",
            ["清空输出"] = "Clear output",
            ["支持类型："] = "Supported types: ",
            ["  ·  PDF 走 SumatraPDF，Office/图片/文本走系统关联程序，HTML 走无头浏览器转 PDF"] =
                "  ·  PDF via SumatraPDF; Office/images/text via system shell; HTML via headless browser to PDF",
            ["  ·  纸张可选 A0–A5（A0/A1 自动换算毫米尺寸，需打印机驱动支持大幅面）"] =
                "  ·  Paper A0–A5 (A0/A1 auto-converted to mm; large-format output requires a capable printer driver)",
            ["请先选择文件或填写 URL。"] = "Please select a file or enter a URL first.",

            // ---- 队列页 ----
            ["任务 ID"] = "Job ID",
            ["文件"] = "File",
            ["打印机"] = "Printer",
            ["份数"] = "Copies",
            ["状态"] = "Status",
            ["入队时间"] = "Enqueued",
            ["完成时间"] = "Finished",
            ["错误"] = "Error",
            ["重试选中"] = "Retry selected",
            ["取消选中"] = "Cancel selected",
            ["删除选中"] = "Remove selected",
            ["清空已完成"] = "Clear finished",
            ["清空全部"] = "Clear all",
            ["等待中"] = "Pending",
            ["打印中"] = "Printing",
            ["已完成"] = "Done",
            ["已取消"] = "Cancelled",
            ["共 {0} · 等待 {1} · 打印中 {2} · 完成 {3} · 失败 {4}"] =
                "Total {0} · pending {1} · printing {2} · done {3} · failed {4}",
            ["确认清空全部队列任务？正在打印的任务会保留。"] =
                "Clear all queue jobs? Jobs currently printing will be kept.",

            // ---- 打印机页 ----
            ["打印机名称"] = "Printer name",
            ["驱动"] = "Driver",
            ["端口"] = "Port",
            ["默认"] = "Default",
            ["纸张规格"] = "Paper sizes",
            ["刷新列表"] = "Refresh list",
            ["设为默认"] = "Set as default",
            ["打开文档"] = "Open docs",
            ["★ 是"] = "★ Yes",
            ["本机共 {0} 台打印机 · 当前默认："] = "{0} printers on this machine · current default: ",
            ["已发送测试页"] = "Test page sent",
            ["测试页"] = "Test page",

            // ---- 局域网打印机扫描 ----
            ["扫描局域网"] = "Scan LAN",
            ["扫描中…"] = "Scanning…",
            ["局域网扫描"] = "LAN scan",
            ["来源"] = "Source",
            ["名称"] = "Name",
            ["地址"] = "Address",
            ["型号"] = "Model",
            ["本机网络打印机"] = "Installed network printers",
            ["网络发现的设备"] = "Discovered devices",
            ["扫描完成：本机网络打印机 {0} 台 · 网络发现 {1} 台 · 耗时 {2} 毫秒"] =
                "Scan done: {0} installed network printers · {1} discovered · {2} ms",
            ["扫描结果仅用于识别设备；如需使用请在 Windows 中添加为网络打印机并安装厂商驱动。"] =
                "Results are for identification only; to use a device, add it as a network printer in Windows and install the vendor driver.",
            ["尚未发现网络打印机。请确认目标设备已联网、已开机并开启了网络发现。"] =
                "No network printers found. Make sure the device is powered on, online, and network discovery is enabled.",
            ["指定网段（可选）："] = "Target subnets (optional):",
            ["端口探测"] = "Port probe",
            ["已探测 {0} 个地址"] = "Probed {0} addresses",
            ["超出上限跳过 {0} 个"] = "{0} skipped (limit reached)",

            // ---- 磁盘清理页 ----
            ["大小"] = "Size",
            ["最后修改"] = "Last modified",
            ["类别"] = "Category",
            ["路径"] = "Path",
            ["删除时移入回收站"] = "Move to Recycle Bin on delete",
            ["扫描"] = "Scan",
            ["全选"] = "Select all",
            ["全不选"] = "Select none",
            ["试运行"] = "Dry run",
            ["确认清理"] = "Clean now",
            ["导出清单"] = "Export list",
            ["发现 {0} 项 · 合计 {1}{2}"] = "{0} items found · total {1}{2}",
            ["（仅显示前 500 项）"] = " (first 500 shown)",
            ["磁盘："] = "Disks: ",
            ["% 已用 / 可用 "] = "% used / free ",
            ["请先勾选要处理的文件。"] = "Please check the files to process first.",
            ["⚠️ 即将删除 {0} 个文件。\n\n仅会删除应用临时目录与系统 Temp 下的文件，且需通过白名单校验。\n{1}\n\n确认继续？"] =
                "⚠️ About to delete {0} file(s).\n\nOnly files under app temp dirs and system Temp will be deleted, subject to whitelist checks.\n{1}\n\nContinue?",
            ["文件将移入回收站，可恢复。"] = "Files will be moved to the Recycle Bin and can be restored.",
            ["文件将被永久删除，不可恢复！"] = "Files will be permanently deleted and cannot be recovered!",
            ["清理确认"] = "Clean up confirmation",
            ["请求 {0} 项 · 处理 {1} 项 · 跳过 {2} 项 · 释放 {3}\n{4}"] =
                "Requested {0} · processed {1} · skipped {2} · freed {3}\n{4}",
            ["错误："] = "Errors: ",
            ["试运行结果"] = "Dry run result",
            ["清理结果"] = "Cleanup result",
            ["清单已导出。"] = "List exported.",
            ["文本文件|*.txt"] = "Text files|*.txt",

            // ---- 设置页 ----
            ["开机自启"] = "Run at Windows startup",
            ["清理时移入回收站"] = "Move to Recycle Bin when cleaning",
            ["尚未检查更新"] = "No update check yet",
            ["服务与网络"] = "Service & Network",
            ["打印与日志"] = "Printing & Logging",
            ["文档与帮助"] = "Docs & Help",
            ["本机指纹"] = "Machine Fingerprint",
            ["自动更新（Gitee）"] = "Auto Update (Gitee)",
            ["界面语言"] = "Interface Language",
            ["WebSocket 端点："] = "WebSocket endpoint:",
            ["与 Web 共用端口，无需单独配置"] = "Shares the port with Web; no separate configuration needed",
            ["Web 文档监听："] = "Web docs listen:",
            ["默认打印机："] = "Default printer:",
            ["保留天数："] = "Keep days:",
            ["最小文件(MB)："] = "Min size (MB):",
            ["白名单目录（每行一个，支持环境变量）："] = "Whitelist dirs (one per line, env vars supported):",
            ["📖 打开文档站点"] = "📖 Open docs",
            ["📖 查看自动更新"] = "📖 Auto-update guide",
            ["默认浏览器打开内嵌文档站（端口 19861）"] = "Opens the embedded docs site in the default browser (port 19861)",
            ["machineId："] = "machineId:",
            ["复制"] = "Copy",
            ["跨浏览器 / 会话稳定的本机标识；作为打印习惯的 machine-key"] =
                "A stable machine identity across browsers/sessions; used as the machine-key for print habits",
            ["更新清单 URL："] = "Update manifest URL:",
            ["保存设置"] = "Save settings",
            ["重新载入"] = "Reload",
            ["取消开机自启"] = "Disable autostart",
            ["尚未检查更新（当前 {0}）"] = "No update check yet (current {0})",
            ["Web 服务运行中："] = "Web service running: ",
            ["Web 服务未运行"] = "Web service not running",
            ["已取消开机自启（需保存设置以持久化）"] = "Autostart disabled (save settings to persist)",
            ["Web 服务未运行。请保存设置后重启应用。"] = "Web service is not running. Save settings and restart the app.",
            ["无法打开文档站点："] = "Cannot open documentation: ",
            ["无法打开文档："] = "Cannot open docs: ",
            ["machineId 已复制"] = "machineId copied",
            ["复制失败："] = "Copy failed: ",
            ["语言已保存，重启应用后生效。"] = "Language saved. Restart the app to apply.",
            ["配置已保存；端口/自启等改动需重启应用生效"] =
                "Settings saved; restart the app to apply port/autostart changes",

            // ---- AO（Active-Online Print 主动在线打印）----
            ["主动在线打印（AO）"] = "Active-Online Print (AO)",
            ["启用 AO 终端："] = "Enable AO terminal:",
            ["中继地址："] = "Relay URL:",
            ["共享密钥："] = "Shared key:",
            ["终端名称："] = "Terminal name:",
            ["AO 状态："] = "AO status:",
            ["在线"] = "online",
            ["离线"] = "offline",
            ["未启用"] = "disabled",
            ["AO 客户端运行中："] = "AO client running: ",
            ["支持 ws:// / wss:// / http(s)://，自动换算为 ws；留空密钥则不校验"] =
                "Accepts ws:// / wss:// / http(s):// (auto-converted to ws); leave the key blank to skip verification",
            ["启用后本终端主动连接中继服务器，接受广域网远程打印任务（命令走白名单，仅允许已配置的命令）"] =
                "When enabled, this terminal connects outbound to the relay server and accepts remote print jobs over the WAN (commands are restricted to the allowlist)",

            // ---- 更新相关 ----
            ["自动更新"] = "Auto update",
            ["重启"] = "Restart",
            ["正在检查更新…"] = "Checking for updates…",
            ["检查失败："] = "Check failed: ",
            ["检查更新失败："] = "Update check failed: ",
            ["检查更新异常："] = "Update check error: ",
            ["发现新版本"] = "New version available",
            ["发现新版本 {0}（当前 {1}）"] = "New version {0} available (current {1})",
            ["发现新版本 {0}（当前 {1}）。是否立即更新？"] = "New version {0} (current {1}). Update now?",
            ["发现新版本 {0}（当前 {1}），点击此处查看更新说明并安装。"] =
                "New version {0} available (current {1}). Click here to review and install.",
            ["自动检查新版本"] = "Auto-check for updates",
            ["检查间隔（小时）："] = "Check interval (hours): ",
            ["启用后：启动延迟约 30 秒检查一次，之后按间隔在后台检查；发现新版本仅托盘提示，不自动下载或打断操作。离线或更新服务不可达时不发请求，网络恢复后自动补查。"] =
                "When enabled: checks once ~30s after startup, then at the set interval in the background. " +
                "A new version only shows a tray notification — no automatic download, no interruption. " +
                "No requests while offline or unreachable; a check is retried automatically once the network is back.",
            ["发现新版本 {0}。是否立即下载并安装？\n（旧程序文件会自动让位备份，可手动还原）"] =
                "New version {0} found. Download and install now?\n(The old program file will be renamed as backup and can be restored manually)",
            ["正在下载并安装…"] = "Downloading and installing…",
            ["更新已安装到 {0}，旧版备份到 {1}。\n是否立即重启？"] =
                "Update installed to {0}, old version backed up to {1}.\nRestart now?",
            ["安装失败："] = "Install failed: ",
            ["更新失败："] = "Update failed: ",
            ["已跳过本次更新"] = "Update skipped",
            ["已是最新版本 {0}"] = "You are on the latest version {0}",
            ["已是最新版本 "] = "You are on the latest version ",
            ["已加入打印队列："] = "Added to print queue: ",
            ["快速打印"] = "Quick print",
            ["本机指纹（跨浏览器/会话稳定）"] = "Machine fingerprint (stable across browsers/sessions)",
            ["获取本机指纹失败："] = "Failed to get machine fingerprint: ",

            // ---- 日志页 ----
            ["自动刷新"] = "Auto refresh",
            ["打开日志目录"] = "Open log folder",
            ["日志目录尚未生成"] = "Log directory not created yet",
            ["暂无 {0} 级别日志。"] = "No {0}-level logs yet.",
            ["读取失败: "] = "Read failed: ",
        };

        /// <summary>入口：在构建任何 UI 之前调用（Main 最前段）。</summary>
        public static void Init()
        {
            try { Configured = Services.SettingsStore.Instance.Current.Language; }
            catch { Configured = Auto; }
            if (string.IsNullOrWhiteSpace(Configured)) Configured = Auto;

            Detected = Detect();
            Current = Resolve(Configured);
        }

        private static string Detect()
        {
            try
            {
                var name = Thread.CurrentThread.CurrentUICulture ??
                           CultureInfo.CurrentUICulture;
                return IsChinese(name) ? ZhCn : En;
            }
            catch { return En; }
        }

        private static bool IsChinese(CultureInfo c) =>
            c != null && string.Equals(c.TwoLetterISOLanguageName, "zh",
                StringComparison.OrdinalIgnoreCase);

        private static string Resolve(string configured)
        {
            switch ((configured ?? Auto).Trim().ToLowerInvariant())
            {
                case "zh-cn":
                case "zh":
                    return ZhCn;
                case "en":
                    return En;
                default:
                    return Detected;
            }
        }

        private static string ResolvedConfig()
        {
            return Current == ZhCn ? ZhCn : En;
        }

        /// <summary>翻译查询：中文即原文；英文查词典，缺失回退原文。</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (Current == ZhCn) return key;
            return _en.TryGetValue(key, out var v) ? v : key;
        }

        /// <summary>格式化翻译（配套 string.Format）。</summary>
        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        /// <summary>支持的语言代码。</summary>
        public static IReadOnlyList<string> Supported => new[] { Auto, ZhCn, En };

        /// <summary>设置页下拉显示名。</summary>
        public static string DisplayName(string code)
        {
            switch (code)
            {
                case Auto: return "跟随系统 / Auto";
                case ZhCn: return "简体中文";
                case En: return "English";
                default: return code;
            }
        }
    }
}

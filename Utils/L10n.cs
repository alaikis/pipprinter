// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 多语言支持（全球主流语言）。
    /// 策略：以中文源串为 key（源语言零转换）；英文为全量基准词典；
    /// 其余语言（ja/ko/de/fr/es/ru/pt/it/nl/ar/pl/tr/uk）提供内嵌词典，
    /// 缺失的词条自动回退英文，再回退中文原文——保证 UI 永不出现空白或键名。
    /// 启动时按 config.language 解析；auto 时检测操作系统 UI 语言并映射到最近的可用词典。
    /// 切换语言需重启应用（词典在构建 UI 时消费）。
    /// </summary>
    public static class L10n
    {
        public const string Auto = "auto";
        public const string ZhCn = "zh-CN";
        public const string En = "en";
        public const string Ja = "ja";
        public const string Ko = "ko";
        public const string De = "de";
        public const string Fr = "fr";
        public const string Es = "es";
        public const string Ru = "ru";
        public const string Pt = "pt";
        public const string It = "it";
        public const string Nl = "nl";
        public const string Ar = "ar";
        public const string Pl = "pl";
        public const string Tr = "tr";
        public const string Uk = "uk";

        /// <summary>配置中的语言设置（auto / 语言代码）。</summary>
        public static string Configured { get; private set; } = Auto;

        /// <summary>当前生效语言。</summary>
        public static string Current { get; private set; } = ZhCn;

        /// <summary>操作系统检测出的语言（已映射到可用词典代码）。</summary>
        public static string Detected { get; private set; } = ZhCn;

        /// <summary>UI 语言切换是否需要重启（词典在构建 UI 时消费）。</summary>
        public static bool NeedsRestart => !string.Equals(Configured, ResolvedConfig(),
            StringComparison.OrdinalIgnoreCase);

        /// <summary>中文源串 → 英文（全量基准）。</summary>
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
            ["待认证"] = "Held for release",
            ["打印中"] = "Printing",
            ["已完成"] = "Done",
            ["已取消"] = "Cancelled",
            ["共 {0} · 等待 {1} · 打印中 {2} · 完成 {3} · 失败 {4}"] =
                "Total {0} · pending {1} · printing {2} · done {3} · failed {4}",
            ["确认清空全部队列任务？正在打印的任务会保留。"] =
                "Clear all queue jobs? Jobs currently printing will be kept.",

            // ---- 排期 / 批次 / 召回 / 推荐（v1.01.010）----
            ["排期"] = "Schedule",
            ["计划任务"] = "Scheduled plan",
            ["批次"] = "Batch",
            ["波次"] = "Wave",
            ["批量打印"] = "Batch print",
            ["召回"] = "Recall",
            ["撤回"] = "Withdraw",
            ["推荐排期"] = "Recommend schedule",
            ["幂等命中"] = "Idempotent hit",
            ["到点自动打印"] = "Auto-print when due",
            ["计划任务已排，到点自动入队打印"] = "Plan scheduled; it will be queued automatically when due",
            ["计划任务已撤回"] = "Scheduled plan withdrawn",
            ["计划任务已提前触发"] = "Scheduled plan triggered early",
            ["批量任务已入队"] = "Batch jobs enqueued",
            ["任务已召回撤下"] = "Job recalled and removed from queue",
            ["出纸后不可撤销，需按审计回执追溯"] = "Irreversible once printed; trace via audit receipt",
            ["无历史数据，已按默认策略推荐均匀分布时段"] = "No history; recommended even distribution by default",
            ["已按近 14 天打印低峰推荐错峰时段"] = "Recommended off-peak slots from the last 14 days of print history",
            ["计划任务列表"] = "Scheduled plans",
            ["暂无计划任务"] = "No scheduled plans",

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
            ["打印到此设备"] = "Print to device",
            ["请先在扫描结果中选中一台带地址的网络打印机。"] = "Please select a network printer with an address from the scan results first.",
            ["选择要打印到此设备的文件"] = "Select a file to print to this device",
            ["发送失败"] = "Send failed",
            ["已发送到此设备"] = "Sent to device",
            ["网络打印机直发（本机无需驱动）：选文件后自动识别 631/IPP 或 9100/RAW"] =
                "Direct network print (no driver needed on this PC): after picking a file, auto-detects 631/IPP or 9100/RAW",

            // ---- 别名（slug）与打印审计 ----
            ["存为别名"] = "Save as alias",
            ["刷新别名"] = "Refresh aliases",
            ["打印审计"] = "Print audit",
            ["别名"] = "Alias",
            ["目标"] = "Target",
            ["模式"] = "Mode",
            ["备注"] = "Note",
            ["请先在扫描结果中选中一台带地址的网络打印机，再存为别名。"] =
                "Please select a network printer with an address from the scan results first, then save it as an alias.",
            ["设备 {0}（{1}）的别名："] = "Alias for device {0} ({1}):",
            ["保存"] = "Save",
            ["保存失败"] = "Save failed",
            ["别名「{0}」已保存（{1}）"] = "Alias \"{0}\" saved ({1})",
            ["别名共 {0} 条：把 host:port 等复杂目标藏在一个友好名称后，打印入口统一智能调度。"] =
                "{0} aliases: each hides a host:port target behind a friendly name; print entry points dispatch intelligently.",
            ["别名（slug）：把 host:port 等复杂目标藏在一个友好名称后，打印入口统一智能调度。"] =
                "Aliases (slug): each hides a host:port target behind a friendly name; print entry points dispatch intelligently.",
            ["最近 {0} 条打印审计（本机）："] = "Last {0} print audit events (local):",
            ["（暂无记录）"] = "(no records)",
            ["发送成功"] = "Sent successfully",

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

        /// <summary>多语言内嵌词典：lang →（中文源串 → 译文）。en 为全量基准。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _dict =
            BuildDict();

        private static Dictionary<string, Dictionary<string, string>> BuildDict()
        {
            var d = new Dictionary<string, Dictionary<string, string>> { [En] = _en };
            d[Ja] = DictJa();
            d[Ko] = DictKo();
            d[De] = DictDe();
            d[Fr] = DictFr();
            d[Es] = DictEs();
            d[Ru] = DictRu();
            d[Pt] = DictPt();
            d[It] = DictIt();
            d[Nl] = DictNl();
            d[Ar] = DictAr();
            d[Pl] = DictPl();
            d[Tr] = DictTr();
            d[Uk] = DictUk();
            return d;
        }

        /// <summary>各语言词典（键=中文源串）。缺失条目自动回退英文，再回退原文。</summary>
        private static Dictionary<string, string> DictJa()
        {
            return new Dictionary<string, string>
            {
["提示"] = "ヒント",
["確認"] = "確認",
["完成"] = "完了",
["失敗"] = "失敗",
["設定"] = "設定",
["準備完了"] = "準備完了",
["未実行"] = "未実行",
["更新"] = "更新",
["参照…"] = "参照…",
["プリンター："] = "プリンター：",
["ポート："] = "ポート：",
["ログレベル："] = "ログレベル：",
["部数："] = "部数：",
["縮尺："] = "縮尺：",
["方向："] = "方向：",
["紙："] = "用紙：",
["ページ："] = "ページ：",
["ファイル / URL："] = "ファイル / URL：",
["プリンター"] = "プリンタ",
["印刷"] = "印刷",
["印刷キュー"] = "印刷キュー",
["ディスククリーンアップ"] = "ディスククリーンアップ",
["ログ"] = "ログ",
["プリンタ"] = "プリンタ",
["メインウィンドウを開く"] = "メインウィンドウを開く",
["クイック印刷…"] = "クイック印刷…",
["プリンタ一覧"] = "プリンタ一覧",
["ドキュメントサイトを開く"] = "ドキュメントサイトを開く",
["マシンのフィンガープリントを見る"] = "マシンのフィンガープリントを見る",
["更新を確認"] = "更新を確認",
["終了"] = "終了",
["即時印刷"] = "即時印刷",
["キューに追加"] = "キューに追加",
["出力をクリア"] = "出力をクリア",
["タスク ID"] = "タスク ID",
["ファイル"] = "ファイル",
["部数"] = "部数",
["状態"] = "状態",
["エラー"] = "エラー",
["待機中"] = "待機中",
["印刷中"] = "印刷中",
["完了済み"] = "完了",
["キャンセル済み"] = "キャンセル済み",
["リトライ"] = "選択をリトライ",
["キャンセル"] = "選択をキャンセル",
["削除"] = "削除",
["完了をクリア"] = "完了をクリア",
["すべてクリア"] = "すべてクリア",
["排期"] = "スケジュール",
["計画タスク"] = "計画タスク",
["バッチ"] = "バッチ",
["ウェーブ"] = "ウェーブ",
["プリンター名"] = "プリンター名",
["ドライバー"] = "ドライバー",
["ポート"] = "ポート",
["既定"] = "既定",
["デフォルトに設定"] = "デフォルトに設定",
["リフレッシュ"] = "一覧を更新",
["テストページ"] = "テストページ",
["スキャン中…"] = "スキャン中…",
["保存"] = "保存",
["サイズ"] = "サイズ",
["カテゴリ"] = "カテゴリ",
["パス"] = "パス",
["スキャン"] = "スキャン",
["全選択"] = "すべて選択",
["すべて解除"] = "すべて解除",
["実行時間"] = "実行時間",
["クリーンアップ"] = "クリーンアップ",
["リストエクスポート"] = "リストエクスポート",
["自動起動"] = "自動起動",
["自動更新（Gitee）"] = "自動更新（Gitee）",
["UI 言語"] = "UI 言語",
["コピー"] = "コピー",
["保存設定"] = "設定を保存",
["リロード"] = "リロード",
["再起動"] = "再起動",
["自動更新"] = "自動更新",
["オンライン"] = "オンライン",
["オフライン"] = "オフライン",
["無効"] = "無効",
["ソース"] = "ソース",
["名前"] = "名前",
["アドレス"] = "アドレス",
["型"] = "型",
["ターゲット"] = "ターゲット",
["モード"] = "モード",
["備考"] = "備考",
["別名"] = "別名",
["印刷監査"] = "印刷監査",
            };
        }
        private static Dictionary<string, string> DictKo()
        {
            return new Dictionary<string, string>
            {
["提示"] = "힌트",
["确认"] = "확인",
["完成"] = "완료",
["失败"] = "실패",
["设置"] = "설정",
["就绪"] = "준비됨",
["未运行"] = "실행 안 됨",
["刷新"] = "새로고침",
["浏览…"] = "찾아보기…",
["打印机："] = "프린터:",
["端口："] = "포트:",
["日志级别："] = "로그 수준:",
["份数："] = "부수:",
["缩放："] = "배율:",
["方向："] = "방향:",
["纸张："] = "용지:",
["页码："] = "페이지:",
["文件 / URL："] = "파일 / URL:",
["打印队列"] = "인쇄 대기열",
["打印机"] = "프린터",
["磁盘清理"] = "디스크 정리",
["日志"] = "로그",
["打开主窗口"] = "메인 창 열기",
["快速打印…"] = "빠른 인쇄…",
["打印机列表"] = "프린터 목록",
["检查更新"] = "업데이트 확인",
["退出"] = "종료",
["立即打印"] = "지금 인쇄",
["加入队列"] = "대기열에 추가",
["清空输出"] = "출력 지우기",
["任务 ID"] = "작업 ID",
["文件"] = "파일",
["份数"] = "부수",
["状态"] = "상태",
["错误"] = "오류",
["等待中"] = "대기",
["打印中"] = "인쇄 중",
["已完成"] = "완료",
["已取消"] = "취소",
["排期"] = "스케줄",
["计划任务"] = "예약 작업",
["批次"] = "배치",
["波次"] = "웨이브",
["批量打印"] = "배치 인쇄",
["召回"] = "회수",
["撤回"] = "취하",
["打印机名称"] = "프린터 이름",
["驱动"] = "드라이버",
["端口"] = "포트",
["默认"] = "기본",
["设为默认"] = "기본으로 설정",
["刷新列表"] = "목록 새로고침",
["测试页"] = "테스트 페이지",
["扫描中…"] = "스캔 중…",
["来源"] = "출처",
["名称"] = "이름",
["地址"] = "주소",
["型号"] = "모델",
["目标"] = "대상",
["模式"] = "모드",
["备注"] = "메모",
["别名"] = "별명",
["保存"] = "저장",
["大小"] = "크기",
["最后修改"] = "최근 수정",
["类别"] = "카테고리",
["路径"] = "경로",
["扫描"] = "스캔",
["全选"] = "전체 선택",
["全不选"] = "전체 해제",
["试运行"] = "예행",
["导出清单"] = "목록 내보내기",
["开机自启"] = "시작 시 자동 실행",
["界面语言"] = "인터페이스 언어",
["复制"] = "복사",
["保存设置"] = "설정 저장",
["重新载入"] = "다시 로드",
["自动更新"] = "자동 업데이트",
["重启"] = "재시작",
["在线"] = "온라인",
["离线"] = "오프라인",
["未启用"] = "비활성",
["快速打印"] = "빠른 인쇄",
            };
        }
        private static Dictionary<string, string> DictDe()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Hinweis",
["确认"] = "OK",
["完成"] = "Fertig",
["失败"] = "Fehler",
["设置"] = "Einstellungen",
["就绪"] = "Bereit",
["未运行"] = "läuft nicht",
["刷新"] = "Aktualisieren",
["浏览…"] = "Durchsuchen…",
["打印机："] = "Drucker:",
["端口："] = "Port:",
["日志级别："] = "Protokollstufe:",
["份数："] = "Kopien:",
["缩放："] = "Einpassen:",
["方向："] = "Ausrichtung:",
["纸张："] = "Papier:",
["页码："] = "Seiten:",
["文件 / URL："] = "Datei / URL:",
["打印队列"] = "Druckwarteschlange",
["打印机"] = "Drucker",
["磁盘清理"] = "Datenträgerbereinigung",
["日志"] = "Protokoll",
["打开主窗口"] = "Hauptfenster öffnen",
["快速打印…"] = "Schnell drucken…",
["打印机列表"] = "Druckerliste",
["检查更新"] = "Nach Updates suchen",
["退出"] = "Beenden",
["立即打印"] = "Jetzt drucken",
["加入队列"] = "In Warteschlange",
["清空输出"] = "Ausgabe leeren",
["任务 ID"] = "Aufgabe-ID",
["文件"] = "Datei",
["份数"] = "Kopien",
["状态"] = "Status",
["错误"] = "Fehler",
["等待中"] = "Wartend",
["打印中"] = "Wird gedruckt",
["已完成"] = "Fertig",
["已取消"] = "Abgebrochen",
["排期"] = "Zeitplanung",
["计划任务"] = "Geplante Aufgabe",
["批次"] = "Partie",
["波次"] = "Welle",
["批量打印"] = "Partiendruck",
["召回"] = "Rückruf",
["撤回"] = "Zurückziehen",
["打印机名称"] = "Druckername",
["驱动"] = "Treiber",
["端口"] = "Port",
["默认"] = "Standard",
["设为默认"] = "Als Standard festlegen",
["刷新列表"] = "Liste aktualisieren",
["测试页"] = "Testseite",
["扫描中…"] = "Wird gescannt…",
["来源"] = "Quelle",
["名称"] = "Name",
["地址"] = "Adresse",
["型号"] = "Modell",
["目标"] = "Ziel",
["模式"] = "Modus",
["备注"] = "Hinweis",
["别名"] = "Alias",
["保存"] = "Speichern",
["大小"] = "Größe",
["最后修改"] = "Zuletzt geändert",
["类别"] = "Kategorie",
["路径"] = "Pfad",
["扫描"] = "Scan",
["全选"] = "Alle auswählen",
["全不选"] = "Nichts auswählen",
["试运行"] = "Dry-Run",
["导出清单"] = "Liste exportieren",
["开机自启"] = "Beim Starten ausführen",
["界面语言"] = "Sprache",
["复制"] = "Kopieren",
["保存设置"] = "Einstellungen speichern",
["重新载入"] = "Neu laden",
["自动更新"] = "Automatische Updates",
["重启"] = "Neu starten",
["在线"] = "online",
["离线"] = "offline",
["未启用"] = "deaktiviert",
["快速打印"] = "Schnell drucken",
            };
        }
        private static Dictionary<string, string> DictFr()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Avis",
["确认"] = "OK",
["完成"] = "Terminé",
["失败"] = "Échec",
["设置"] = "Paramètres",
["就绪"] = "Prêt",
["未运行"] = "non actif",
["刷新"] = "Actualiser",
["浏览…"] = "Parcourir…",
["打印机："] = "Imprimante :",
["端口："] = "Port :",
["日志级别："] = "Niveau de journal :",
["份数："] = "Copies :",
["缩放："] = "Ajustement :",
["方向："] = "Orientation :",
["纸张："] = "Papier :",
["页码："] = "Pages :",
["文件 / URL："] = "Fichier / URL :",
["打印队列"] = "File d'impression",
["打印机"] = "Imprimante",
["磁盘清理"] = "Nettoyage du disque",
["日志"] = "Journaux",
["打开主窗口"] = "Ouvrir la fenêtre principale",
["快速打印…"] = "Impression rapide…",
["打印机列表"] = "Liste des imprimantes",
["检查更新"] = "Vérifier les mises à jour",
["退出"] = "Quitter",
["立即打印"] = "Imprimer",
["加入队列"] = "Ajouter à la file",
["清空输出"] = "Effacer la sortie",
["任务 ID"] = "ID de la tâche",
["文件"] = "Fichier",
["份数"] = "Copies",
["状态"] = "Statut",
["错误"] = "Erreur",
["等待中"] = "En attente",
["打印中"] = "Impression",
["已完成"] = "Terminé",
["已取消"] = "Annulé",
["排期"] = "Planification",
["计划任务"] = "Tâche planifiée",
["批次"] = "Lot",
["波次"] = "Vague",
["批量打印"] = "Impression par lot",
["召回"] = "Rappel",
["撤回"] = "Retrait",
["打印机名称"] = "Nom de l'imprimante",
["驱动"] = "Pilote",
["端口"] = "Port",
["默认"] = "Par défaut",
["设为默认"] = "Définir par défaut",
["刷新列表"] = "Actualiser la liste",
["测试页"] = "Page de test",
["扫描中…"] = "Analyse…",
["来源"] = "Source",
["名称"] = "Nom",
["地址"] = "Adresse",
["型号"] = "Modèle",
["目标"] = "Cible",
["模式"] = "Mode",
["备注"] = "Note",
["别名"] = "Alias",
["保存"] = "Enregistrer",
["大小"] = "Taille",
["最后修改"] = "Modifié le",
["类别"] = "Catégorie",
["路径"] = "Chemin",
["扫描"] = "Analyser",
["全选"] = "Tout sélectionner",
["全不选"] = "Aucun",
["试运行"] = "Pré-visualisation",
["导出清单"] = "Exporter la liste",
["开机自启"] = "Démarrage automatique",
["界面语言"] = "Langue",
["复制"] = "Copier",
["保存设置"] = "Enregistrer les paramètres",
["重新载入"] = "Recharger",
["自动更新"] = "Mise à jour auto",
["重启"] = "Redémarrer",
["在线"] = "en ligne",
["离线"] = "hors ligne",
["未启用"] = "désactivé",
["快速打印"] = "Impression rapide",
            };
        }
        private static Dictionary<string, string> DictEs()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Aviso",
["确认"] = "Aceptar",
["完成"] = "Completado",
["失败"] = "Error",
["设置"] = "Configuración",
["就绪"] = "Listo",
["未运行"] = "no en ejecución",
["刷新"] = "Actualizar",
["浏览…"] = "Examinar…",
["打印机："] = "Impresora:",
["端口："] = "Puerto:",
["日志级别："] = "Nivel de registro:",
["份数："] = "Copias:",
["缩放："] = "Ajustar:",
["方向："] = "Orientación:",
["纸张："] = "Papel:",
["页码："] = "Páginas:",
["文件 / URL："] = "Archivo / URL:",
["打印队列"] = "Cola de impresión",
["打印机"] = "Impresora",
["磁盘清理"] = "Limpieza de disco",
["日志"] = "Registros",
["打开主窗口"] = "Abrir ventana principal",
["快速打印…"] = "Impresión rápida…",
["打印机列表"] = "Lista de impresoras",
["检查更新"] = "Buscar actualizaciones",
["退出"] = "Salir",
["立即打印"] = "Imprimir",
["加入队列"] = "Añadir a la cola",
["清空输出"] = "Vaciar salida",
["任务 ID"] = "ID de tarea",
["文件"] = "Archivo",
["份数"] = "Copias",
["状态"] = "Estado",
["错误"] = "Error",
["等待中"] = "Pendiente",
["打印中"] = "Imprimiendo",
["已完成"] = "Completado",
["已取消"] = "Cancelado",
["排期"] = "Programación",
["计划任务"] = "Tarea programada",
["批次"] = "Lote",
["波次"] = "Ola",
["批量打印"] = "Impresión por lotes",
["召回"] = "Recuperar",
["撤回"] = "Retirar",
["打印机名称"] = "Nombre de impresora",
["驱动"] = "Controlador",
["端口"] = "Puerto",
["默认"] = "Predeterminado",
["设为默认"] = "Establecer como predeterminado",
["刷新列表"] = "Actualizar lista",
["测试页"] = "Página de prueba",
["扫描中…"] = "Escaneando…",
["来源"] = "Origen",
["名称"] = "Nombre",
["地址"] = "Dirección",
["型号"] = "Modelo",
["目标"] = "Destino",
["模式"] = "Modo",
["备注"] = "Nota",
["别名"] = "Alias",
["保存"] = "Guardar",
["大小"] = "Tamaño",
["最后修改"] = "Modificado",
["类别"] = "Categoría",
["路径"] = "Ruta",
["扫描"] = "Escanear",
["全选"] = "Seleccionar todo",
["全不选"] = "Ninguno",
["试运行"] = "Prueba",
["导出清单"] = "Exportar lista",
["开机自启"] = "Iniciar con Windows",
["界面语言"] = "Idioma",
["复制"] = "Copiar",
["保存设置"] = "Guardar configuración",
["重新载入"] = "Recargar",
["自动更新"] = "Actualización automática",
["重启"] = "Reiniciar",
["在线"] = "en línea",
["离线"] = "fuera de línea",
["未启用"] = "deshabilitado",
["快速打印"] = "Impresión rápida",
            };
        }
        private static Dictionary<string, string> DictRu()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Примечание",
["确认"] = "OK",
["完成"] = "Готово",
["失败"] = "Ошибка",
["设置"] = "Настройки",
["就绪"] = "Готово",
["未运行"] = "не запущено",
["刷新"] = "Обновить",
["浏览…"] = "Обзор…",
["打印机："] = "Принтер:",
["端口："] = "Порт:",
["日志级别："] = "Уровень журнала:",
["份数："] = "Копии:",
["缩放："] = "Масштаб:",
["方向："] = "Ориентация:",
["纸张："] = "Бумага:",
["页码："] = "Страницы:",
["文件 / URL："] = "Файл / URL:",
["打印队列"] = "Очередь печати",
["打印机"] = "Принтер",
["磁盘清理"] = "Очистка диска",
["日志"] = "Журналы",
["打开主窗口"] = "Открыть основное окно",
["快速打印…"] = "Быстрая печать…",
["打印机列表"] = "Список принтеров",
["检查更新"] = "Проверить обновления",
["退出"] = "Выход",
["立即打印"] = "Печатать",
["加入队列"] = "В очередь",
["清空输出"] = "Очистить вывод",
["任务 ID"] = "ID задачи",
["文件"] = "Файл",
["份数"] = "Копии",
["状态"] = "Состояние",
["错误"] = "Ошибка",
["等待中"] = "В ожидании",
["打印中"] = "Печать",
["已完成"] = "Выполнено",
["已取消"] = "Отменено",
["排期"] = "Планирование",
["计划任务"] = "Запланированная задача",
["批次"] = "Партия",
["波次"] = "Волна",
["批量打印"] = "Печать партиями",
["召回"] = "Отзыв",
["撤回"] = "Отозвать",
["打印机名称"] = "Имя принтера",
["驱动"] = "Драйвер",
["端口"] = "Порт",
["默认"] = "По умолчанию",
["设为默认"] = "Сделать стандартным",
["刷新列表"] = "Обновить список",
["测试页"] = "Тестовая страница",
["扫描中…"] = "Сканирование…",
["来源"] = "Источник",
["名称"] = "Имя",
["地址"] = "Адрес",
["型号"] = "Модель",
["目标"] = "Цель",
["模式"] = "Режим",
["备注"] = "Заметка",
["别名"] = "Псевдоним",
["保存"] = "Сохранить",
["大小"] = "Размер",
["最后修改"] = "Изменено",
["类别"] = "Категория",
["路径"] = "Путь",
["扫描"] = "Сканировать",
["全选"] = "Выделить все",
["全不选"] = "Ничего",
["试运行"] = "Проверка",
["导出清单"] = "Экспортировать список",
["开机自启"] = "Запуск при старте",
["界面语言"] = "Язык",
["复制"] = "Копировать",
["保存设置"] = "Сохранить настройки",
["重新载入"] = "Перезагрузить",
["自动更新"] = "Автообновление",
["重启"] = "Перезапустить",
["在线"] = "в сети",
["离线"] = "не в сети",
["未启用"] = "отключено",
["快速打印"] = "Быстрая печать",
            };
        }
        private static Dictionary<string, string> DictPt()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Aviso",
["确认"] = "OK",
["完成"] = "Concluído",
["失败"] = "Falhou",
["设置"] = "Configurações",
["就绪"] = "Pronto",
["未运行"] = "não em execução",
["刷新"] = "Atualizar",
["浏览…"] = "Procurar…",
["打印机："] = "Impressora:",
["端口："] = "Porta:",
["日志级别："] = "Nível de log:",
["份数："] = "Cópias:",
["缩放："] = "Ajustar:",
["方向："] = "Orientação:",
["纸张："] = "Papel:",
["页码："] = "Páginas:",
["文件 / URL："] = "Arquivo / URL:",
["打印队列"] = "Fila de impressão",
["打印机"] = "Impressora",
["磁盘清理"] = "Limpeza de disco",
["日志"] = "Registros",
["打开主窗口"] = "Abrir janela principal",
["快速打印…"] = "Impressão rápida…",
["打印机列表"] = "Lista de impressoras",
["检查更新"] = "Verificar atualizações",
["退出"] = "Sair",
["立即打印"] = "Imprimir",
["加入队列"] = "Adicionar à fila",
["清空输出"] = "Limpar saída",
["任务 ID"] = "ID da tarefa",
["文件"] = "Arquivo",
["份数"] = "Cópias",
["状态"] = "Status",
["错误"] = "Erro",
["等待中"] = "Pendente",
["打印中"] = "Imprimindo",
["已完成"] = "Concluído",
["已取消"] = "Cancelado",
["排期"] = "Agendamento",
["计划任务"] = "Tarefa agendada",
["批次"] = "Lote",
["波次"] = "Onda",
["批量打印"] = "Impressão por lotes",
["召回"] = "Recuperar",
["撤回"] = "Retirar",
["打印机名称"] = "Nome da impressora",
["驱动"] = "Driver",
["端口"] = "Porta",
["默认"] = "Padrão",
["设为默认"] = "Definir como padrão",
["刷新列表"] = "Atualizar lista",
["测试页"] = "Página de teste",
["扫描中…"] = "Varrendo…",
["来源"] = "Origem",
["名称"] = "Nome",
["地址"] = "Endereço",
["型号"] = "Modelo",
["目标"] = "Destino",
["模式"] = "Modo",
["备注"] = "Nota",
["别名"] = "Alias",
["保存"] = "Salvar",
["大小"] = "Tamanho",
["最后修改"] = "Modificado",
["类别"] = "Categoria",
["路径"] = "Caminho",
["扫描"] = "Varrer",
["全选"] = "Selecionar tudo",
["全不选"] = "Nenhum",
["试运行"] = "Simulação",
["导出清单"] = "Exportar lista",
["开机自启"] = "Iniciar com o Windows",
["界面语言"] = "Idioma",
["复制"] = "Copiar",
["保存设置"] = "Salvar configurações",
["重新载入"] = "Recarregar",
["自动更新"] = "Atualização automática",
["重启"] = "Reiniciar",
["在线"] = "online",
["离线"] = "offline",
["未启用"] = "desativado",
["快速打印"] = "Impressão rápida",
            };
        }
        private static Dictionary<string, string> DictIt()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Avviso",
["确认"] = "OK",
["完成"] = "Completato",
["失败"] = "Erro",
["设置"] = "Impostazioni",
["就绪"] = "Pronto",
["未运行"] = "non attivo",
["刷新"] = "Aggiorna",
["浏览…"] = "Sfoglia…",
["打印机："] = "Stampante:",
["端口："] = "Porta:",
["日志级别："] = "Livello registro:",
["份数："] = "Copie:",
["缩放："] = "Adatta:",
["方向："] = "Orientamento:",
["纸张："] = "Carta:",
["页码："] = "Pagine:",
["文件 / URL："] = "File / URL:",
["打印队列"] = "Coda di stampa",
["打印机"] = "Stampante",
["磁盘清理"] = "Pulizia disco",
["日志"] = "Registri",
["打开主窗口"] = "Apri finestra principale",
["快速打印…"] = "Stampa rapida…",
["打印机列表"] = "Elenco stampanti",
["检查更新"] = "Cerca aggiornamenti",
["退出"] = "Esci",
["立即打印"] = "Stampa",
["加入队列"] = "Aggiungi alla coda",
["清空输出"] = "Svuota output",
["任务 ID"] = "ID attività",
["文件"] = "File",
["份数"] = "Copie",
["状态"] = "Stato",
["错误"] = "Errore",
["等待中"] = "In attesa",
["打印中"] = "In stampa",
["已完成"] = "Completato",
["已取消"] = "Annullato",
["排期"] = "Pianificazione",
["计划任务"] = "Attività pianificata",
["批次"] = "Lotto",
["波次"] = "Onda",
["批量打印"] = "Stampa per lotti",
["召回"] = "Recupera",
["撤回"] = "Ritira",
["打印机名称"] = "Nome stampante",
["驱动"] = "Driver",
["端口"] = "Porta",
["默认"] = "Predefinito",
["设为默认"] = "Imposta come predefinito",
["刷新列表"] = "Aggiorna elenco",
["测试页"] = "Pagina di prova",
["扫描中…"] = "Scansione…",
["来源"] = "Origine",
["名称"] = "Nome",
["地址"] = "Indirizzo",
["型号"] = "Modello",
["目标"] = "Destinazione",
["模式"] = "Modalità",
["备注"] = "Nota",
["别名"] = "Alias",
["保存"] = "Salva",
["大小"] = "Dimensione",
["最后修改"] = "Modificato",
["类别"] = "Categoria",
["路径"] = "Percorso",
["扫描"] = "Scansiona",
["全选"] = "Seleziona tutto",
["全不选"] = "Nessuno",
["试运行"] = "Prova",
["导出清单"] = "Esporta elenco",
["开机自启"] = "Avvio con Windows",
["界面语言"] = "Lingua",
["复制"] = "Copia",
["保存设置"] = "Salva impostazioni",
["重新载入"] = "Ricarica",
["自动更新"] = "Aggiornamenti automatici",
["重启"] = "Riavvia",
["在线"] = "online",
["离线"] = "offline",
["未启用"] = "disabilitato",
["快速打印"] = "Stampa rapida",
            };
        }
        private static Dictionary<string, string> DictNl()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Melding",
["确认"] = "OK",
["完成"] = "Klaar",
["失败"] = "Mislukt",
["设置"] = "Instellingen",
["就绪"] = "Klaar",
["未运行"] = "niet actief",
["刷新"] = "Vernieuwen",
["浏览…"] = "Bladeren…",
["打印机："] = "Printer:",
["端口："] = "Poort:",
["日志级别："] = "Logniveau:",
["份数："] = "Kopieën:",
["缩放："] = "Aanpassen:",
["方向："] = "Orientatie:",
["纸张："] = "Papier:",
["页码："] = "Pagina's:",
["文件 / URL："] = "Bestand / URL:",
["打印队列"] = "Afdrukwachtrij",
["打印机"] = "Printer",
["磁盘清理"] = "Schijf opruimen",
["日志"] = "Logboeken",
["打开主窗口"] = "Hoofdvenster openen",
["快速打印…"] = "Snel afdrukken…",
["打印机列表"] = "Printerlijst",
["检查更新"] = "Updates controleren",
["退出"] = "Beëindig",
["立即打印"] = "Afdrukken",
["加入队列"] = "Toevoegen aan wachtrij",
["清空输出"] = "Uitvoer wissen",
["任务 ID"] = "Taak-ID",
["文件"] = "Bestand",
["份数"] = "Kopieën",
["状态"] = "Status",
["错误"] = "Fout",
["等待中"] = "Wachtend",
["打印中"] = "Bezig met afdrukken",
["已完成"] = "Klaar",
["已取消"] = "Geannuleerd",
["排期"] = "Planning",
["计划任务"] = "Geplande taak",
["批次"] = "Partij",
["波次"] = "Golf",
["批量打印"] = "Partijprinten",
["召回"] = "Herroepen",
["撤回"] = "Intrekken",
["打印机名称"] = "Printernaam",
["驱动"] = "Stuurprogramma",
["端口"] = "Poort",
["默认"] = "Standaard",
["设为默认"] = "Als standaard instellen",
["刷新列表"] = "Lijst vernieuwen",
["测试页"] = "Testpagina",
["扫描中…"] = "Scannen…",
["来源"] = "Bron",
["名称"] = "Naam",
["地址"] = "Adres",
["型号"] = "Model",
["目标"] = "Doel",
["模式"] = "Modus",
["备注"] = "Opmerking",
["别名"] = "Alias",
["保存"] = "Opslaan",
["大小"] = "Grootte",
["最后修改"] = "Laatst gewijzigd",
["类别"] = "Categorie",
["路径"] = "Pad",
["扫描"] = "Scannen",
["全选"] = "Alles selecteren",
["全不选"] = "Geen",
["试运行"] = "Proefdraai",
["导出清单"] = "Lijst exporteren",
["开机自启"] = "Opstarten met Windows",
["界面语言"] = "Taal",
["复制"] = "Kopiëren",
["保存设置"] = "Instellingen opslaan",
["重新载入"] = "Herladen",
["自动更新"] = "Automatische updates",
["重启"] = "Herstarten",
["在线"] = "online",
["离线"] = "offline",
["未启用"] = "uitgeschakeld",
["快速打印"] = "Snel afdrukken",
            };
        }
        private static Dictionary<string, string> DictAr()
        {
            return new Dictionary<string, string>
            {
["提示"] = "تنبيه",
["确认"] = "حسنًا",
["完成"] = "تم",
["失败"] = "فشل",
["设置"] = "الإعدادات",
["就绪"] = "جاهز",
["未运行"] = "غير نشط",
["刷新"] = "تحديث",
["浏览…"] = "تصفح…",
["打印机："] = "الطابعة:",
["端口："] = "المنفذ:",
["日志级别："] = "مستوى السجل:",
["份数："] = "نسخ:",
["缩放："] = "ملاءمة:",
["方向："] = "الاتجاه:",
["纸张："] = "الورق:",
["页码："] = "الصفحات:",
["文件 / URL："] = "ملف / رابط:",
["打印队列"] = "قائمة الطباعة",
["打印机"] = "الطابعة",
["磁盘清理"] = "تنظيف القرص",
["日志"] = "السجلات",
["打开主窗口"] = "فتح النافذة الرئيسية",
["快速打印…"] = "طباعة سريعة…",
["打印机列表"] = "قائمة الطابعات",
["检查更新"] = "التحقق من التحديثات",
["退出"] = "خروج",
["立即打印"] = "طباعة",
["加入队列"] = "إضافة إلى القائمة",
["清空输出"] = "مسح الإخراج",
["任务 ID"] = "معرّف المهمة",
["文件"] = "الملف",
["份数"] = "النسخ",
["状态"] = "الحالة",
["错误"] = "خطأ",
["等待中"] = "قيد الانتظار",
["打印ing"] = "جارٍ الطباعة",
["已完成"] = "مكتمل",
["已取消"] = "ملغى",
["排期"] = "الجدولة",
["计划任务"] = "مهمة مجدولة",
["批次"] = "دفعه",
["波次"] = "موجة",
["批量打印"] = "طباعة دفعات",
["召回"] = "استدعاء",
["撤回"] = "سحب",
["打印机名称"] = "اسم الطابعة",
["驱动"] = "المساريف",
["端口"] = "المنفذ",
["默认"] = "افتراضي",
["设为默认"] = "تعيين كافتراضي",
["刷新列表"] = "تحديث القائمة",
["测试页"] = "صفحة الاختبار",
["扫描中…"] = "جارٍ الفحص…",
["来源"] = "المصدر",
["名称"] = "الاسم",
["地址"] = "العنوان",
["型号"] = "الموديل",
["目标"] = "الهدف",
["模式"] = "الوضع",
["备注"] = "ملاحظة",
["别名"] = "اسم آخر",
["保存"] = "حفظ",
["大小"] = "الحجم",
["最后修改"] = "آخر تعديل",
["类别"] = "الفئة",
["路径"] = "المسار",
["扫描"] = "فحص",
["全选"] = "تحديد الكل",
["全不选"] = "لا شيء",
["试运行"] = "تشغيل تجريبي",
["导出清单"] = "تصدير القائمة",
["开机自启"] = "التشغيل مع ويندوز",
["界面语言"] = "اللغة",
["复制"] = "نسخ",
["保存设置"] = "حفظ الإعدادات",
["重新载入"] = "إعادة تحميل",
["自动更新"] = "تحديث تلقائي",
["重启"] = "إعادة تشغيل",
["在线"] = "متصل",
["离线"] = "غير متصل",
["未启用"] = "معطل",
["快速打印"] = "طباعة سريعة",
            };
        }
        private static Dictionary<string, string> DictPl()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Wskazówka",
["确认"] = "OK",
["完成"] = "Zakończone",
["失败"] = "Niepowodzenie",
["设置"] = "Ustawienia",
["就绪"] = "Gotowe",
["未运行"] = "nie uruchomione",
["刷新"] = "Odśwież",
["浏览…"] = "Przeglądaj…",
["打印机："] = "Drukarka:",
["端口："] = "Port:",
["日志级别："] = "Poziom dziennika:",
["份数："] = "Kopie:",
["缩放："] = "Skalowanie:",
["方向："] = "Orientacja:",
["纸张："] = "Papier:",
["页码："] = "Strony:",
["文件 / URL："] = "Plik / URL:",
["打印队列"] = "Kolejka wydruku",
["打印机"] = "Drukarka",
["磁盘清理"] = "Oczyszczanie dysku",
["日志"] = "Dzienniki",
["打开主窗口"] = "Otwórz główne okno",
["快速打印…"] = "Szybki wydruk…",
["打印机列表"] = "Lista drukarek",
["检查更新"] = "Sprawdź aktualizacje",
["退出"] = "Zamknij",
["立即打印"] = "Drukuj teraz",
["加入队列"] = "Do kolejki",
["清空输出"] = "Wyczyść wyjście",
["任务 ID"] = "ID zadania",
["文件"] = "Plik",
["份数"] = "Kopie",
["状态"] = "Status",
["错误"] = "Błąd",
["等待中"] = "Oczekujące",
["打印中"] = "Drukowanie",
["已完成"] = "Zakończone",
["已取消"] = "Anulowane",
["排期"] = "Planowanie",
["计划任务"] = "Zaplanowane zadanie",
["批次"] = "Partia",
["波次"] = "Fala",
["批量打印"] = "Wydruk partiami",
["召回"] = "Odwołanie",
["撤回"] = "Wycofaj",
["打印机名称"] = "Nazwa drukarki",
["驱动"] = "Sterownik",
["端口"] = "Port",
["默认"] = "Domyślne",
["设为默认"] = "Ustaw jako domyślne",
["刷新列表"] = "Odśwież listę",
["测试页"] = "Strona testowa",
["扫描中…"] = "Skanowanie…",
["来源"] = "Źródło",
["名称"] = "Nazwa",
["地址"] = "Adres",
["型号"] = "Model",
["目标"] = "Cel",
["模式"] = "Tryb",
["备注"] = "Uwagi",
["别名"] = "Alias",
["保存"] = "Zapisz",
["大小"] = "Rozmiar",
["最后修改"] = "Zmodyfikowano",
["类别"] = "Kategoria",
["路径"] = "Ścieżka",
["扫描"] = "Skanuj",
["全选"] = "Zaznacz wszystkie",
["全不选"] = "Odznacz wszystkie",
["试运行"] = "Testuj",
["导出清单"] = "Eksportuj listę",
["开机自启"] = "Uruchamiaj przy starcie",
["界面语言"] = "Język",
["复制"] = "Kopiuj",
["保存设置"] = "Zapisz ustawienia",
["重新载入"] = "Przeładuj",
["自动更新"] = "Aktualizuj automatycznie",
["重启"] = "Uruchom ponownie",
["在线"] = "online",
["离线"] = "offline",
["未启用"] = "wyłączony",
["快速打印"] = "Szybki wydruk",
            };
        }
        private static Dictionary<string, string> DictTr()
        {
            return new Dictionary<string, string>
            {
["提示"] = "İpucu",
["确认"] = "Tamam",
["完成"] = "Tamamlandı",
["失败"] = "Başarısız",
["设置"] = "Ayarlar",
["就绪"] = "Hazır",
["未运行"] = "çalışmıyor",
["刷新"] = "Yenile",
["浏览…"] = "Gözat…",
["打印机："] = "Yazıcı:",
["端口："] = "Port:",
["日志级别："] = "Günlük düzeyi:",
["份数："] = "Kopya:",
["缩放："] = "Ölçekleme:",
["方向："] = "Yerleşim:",
["纸张："] = "Kâğıt:",
["页码："] = "Sayfalar:",
["文件 / URL："] = "Dosya / URL:",
["打印队列"] = "Yazım kuyruğu",
["打印机"] = "Yazıcı",
["磁盘清理"] = "Disk temizliği",
["日志"] = "Günlükler",
["打开主窗口"] = "Ana pencereyi aç",
["快速打印…"] = "Hızlı yazdırma…",
["打印机列表"] = "Yazıcı listesi",
["检查更新"] = "Güncelleştirmeleri kontrol et",
["退出"] = "Çık",
["立即打印"] = "Şimdi yazdır",
["加入队列"] = "Kuyruğa ekle",
["清空输出"] = "Çıktıyı temizle",
["任务 ID"] = "Görev ID",
["文件"] = "Dosya",
["份数"] = "Kopya",
["状态"] = "Durum",
["错误"] = "Hata",
["等待中"] = "Bekleniyor",
["打印中"] = "Yazıyor",
["已完成"] = "Tamamlandı",
["已取消"] = "İptal edildi",
["排期"] = "Zamanlama",
["计划任务"] = "Planlanmış görev",
["批次"] = "Toplu",
["波次"] = "Dalga",
["批量打印"] = "Toplu yazdırma",
["召回"] = "Geri çağır",
["撤回"] = "Geri al",
["打印机名称"] = "Yazıcı adı",
["驱动"] = "Sürücü",
["端口"] = "Port",
["默认"] = "Varsayılan",
["设为默认"] = "Varsayılan yap",
["刷新列表"] = "Listeyi yenile",
["测试页"] = "Test sayfası",
["扫描中…"] = "Taranıyor…",
["来源"] = "Kaynak",
["名称"] = "Ad",
["地址"] = "Adres",
["型号"] = "Model",
["目标"] = "Hedef",
["模式"] = "Mod",
["备注"] = "Notlar",
["别名"] = "Takma ad",
["保存"] = "Kaydet",
["大小"] = "Boyut",
["最后修改"] = "Değiştirilme",
["类别"] = "Kategori",
["路径"] = "Yol",
["扫描"] = "Tara",
["全选"] = "Tümünü seç",
["全不选"] = "Seçimi kaldır",
["试运行"] = "Deneme",
["导出清单"] = "Listeyi dışa aktar",
["开机自启"] = "Başlangıçta çalıştır",
["界面语言"] = "Dil",
["复制"] = "Kopyala",
["保存设置"] = "Ayarları kaydet",
["重新载入"] = "Yeniden yükle",
["自动更新"] = "Otomatik güncelleme",
["重启"] = "Yeniden başlat",
["在线"] = "çevrimiçi",
["离线"] = "çevrimdışı",
["未启用"] = "devre dışı",
["快速打印"] = "Hızlı yazdırma",
            };
        }
        private static Dictionary<string, string> DictUk()
        {
            return new Dictionary<string, string>
            {
["提示"] = "Підказка",
["确认"] = "OK",
["完成"] = "Завершено",
["失败"] = "Помилка",
["设置"] = "Налаштування",
["就绪"] = "Готово",
["未运行"] = "не запущено",
["刷新"] = "Оновити",
["浏览…"] = "Переглянути…",
["打印机："] = "Принтер:",
["端口："] = "Порт:",
["日志级别："] = "Рівень журналу:",
["份数："] = "Копії:",
["缩放："] = "Масштаб:",
["方向："] = "Орієнтація:",
["纸张："] = "Папір:",
["页码："] = "Сторінки:",
["文件 / URL："] = "Файл / URL:",
["打印队列"] = "Черга друку",
["打印机"] = "Принтер",
["磁盘清理"] = "Очищення диска",
["日志"] = "Журнали",
["打开主窗口"] = "Відкрити головне вікно",
["快速打印…"] = "Швидкий друк…",
["打印机列表"] = "Список принтерів",
["检查更新"] = "Перевірити оновлення",
["退出"] = "Вийти",
["立即打印"] = "Друкувати",
["加入队列"] = "До черги",
["清空输出"] = "Очистити вивід",
["任务 ID"] = "ID завдання",
["文件"] = "Файл",
["份数"] = "Копії",
["状态"] = "Стан",
["错误"] = "Помилка",
["等待中"] = "Очікує",
["打印中"] = "Друк",
["已完成"] = "Завершено",
["已取消"] = "Скасовано",
["排期"] = "Планування",
["计划任务"] = "Заплановане завдання",
["批次"] = "Парта",
["波次"] = "Хвиля",
["批量打印"] = "Партийний друк",
["召回"] = "Відкликання",
["撤回"] = "Відкликати",
["打印机名称"] = "Назва принтера",
["驱动"] = "Драйвер",
["端口"] = "Порт",
["默认"] = "За замовчуванням",
["设为默认"] = "Зробити типовим",
["刷新列表"] = "Оновити список",
["测试页"] = "Тестовий аркуш",
["扫描中…"] = "Сканування…",
["来源"] = "Джерело",
["名称"] = "Назва",
["地址"] = "Адреса",
["型号"] = "Модель",
["目标"] = "Ціль",
["模式"] = "Режим",
["备注"] = "Примітки",
["别名"] = "Псевдонім",
["保存"] = "Зберегти",
["大小"] = "Розмір",
["最后修改"] = "Змінено",
["类别"] = "Категорія",
["路径"] = "Шлях",
["扫描"] = "Сканувати",
["全选"] = "Виділити все",
["全不选"] = "Зняти виділення",
["试运行"] = "Перевірка",
["导出清单"] = "Експортувати список",
["开机自启"] = "Запуск при старті",
["界面语言"] = "Мова",
["复制"] = "Копіювати",
["保存设置"] = "Зберегти налаштування",
["重新载入"] = "Перезавантажити",
["自动更新"] = "Автоматичне оновлення",
["重启"] = "Перезапустити",
["在线"] = "у мережі",
["离线"] = "поза мережею",
["未启用"] = "вимкнено",
["快速打印"] = "Швидкий друк",
            };
        }

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
                var c = Thread.CurrentThread.CurrentUICulture ?? CultureInfo.CurrentUICulture;
                return MapDetected(c);
            }
            catch { return En; }
        }

        /// <summary>把操作系统 UI 语言映射到最近的可用词典；未知语言回退英文。</summary>
        private static string MapDetected(CultureInfo c)
        {
            if (c == null) return En;
            var lang = (c.TwoLetterISOLanguageName ?? "").ToLowerInvariant();
            var region = (c.Name ?? "").Split('-').Length > 1 ? c.Name.Split('-')[1].ToUpperInvariant() : "";
            switch (lang)
            {
                case "zh": return ZhCn;
                case "ja": return Ja;
                case "ko": return Ko;
                case "de": return De;
                case "fr": return Fr;
                case "es": return Es;
                case "ru": return Ru;
                case "pt": return Pt; // 巴西/葡萄牙语均用 pt
                case "it": return It;
                case "nl": return Nl;
                case "ar": return Ar;
                case "pl": return Pl;
                case "tr": return Tr;
                case "uk": return Uk;
                default: return En;
            }
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
                case "ja":
                case "ko":
                case "de":
                case "fr":
                case "es":
                case "ru":
                case "pt":
                case "it":
                case "nl":
                case "ar":
                case "pl":
                case "tr":
                case "uk":
                    return configured.Trim().ToLowerInvariant();
                default:
                    return Detected;
            }
        }

        private static string ResolvedConfig()
        {
            // 把当前生效语言归一为 config 可选值；中文归 zh-CN，其余按代码
            if (string.Equals(Current, ZhCn, StringComparison.OrdinalIgnoreCase)) return ZhCn;
            if (string.Equals(Current, En, StringComparison.OrdinalIgnoreCase)) return En;
            return Current.ToLowerInvariant();
        }

        /// <summary>翻译查询：中文即原文；其余语言查内嵌词典，缺失回退英文，再回退原文。</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (string.Equals(Current, ZhCn, StringComparison.OrdinalIgnoreCase)) return key;
            if (_dict.TryGetValue(Current, out var d) && d.TryGetValue(key, out var v)) return v;
            return _en.TryGetValue(key, out var ev) ? ev : key;
        }

        /// <summary>格式化翻译（配套 string.Format）。</summary>
        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        /// <summary>支持的语言代码（含 auto）。</summary>
        public static IReadOnlyList<string> Supported => new[]
        {
            Auto, ZhCn, En, Ja, Ko, De, Fr, Es, Ru, Pt, It, Nl, Ar, Pl, Tr, Uk
        };

        /// <summary>设置页下拉显示名（用该语言自身的称呼）。</summary>
        public static string DisplayName(string code)
        {
            switch (code?.ToLowerInvariant())
            {
                case null: return "";
                case "auto": return "跟随系统 / Auto";
                case "zh-cn": case "zh": return "简体中文";
                case "en": return "English";
                case "ja": return "日本語";
                case "ko": return "한국어";
                case "de": return "Deutsch";
                case "fr": return "Français";
                case "es": return "Español";
                case "ru": return "Русский";
                case "pt": return "Português";
                case "it": return "Italiano";
                case "nl": return "Nederlands";
                case "ar": return "العربية";
                case "pl": return "Polski";
                case "tr": return "Türkçe";
                case "uk": return "Українська";
                default: return code;
            }
        }
    }
}

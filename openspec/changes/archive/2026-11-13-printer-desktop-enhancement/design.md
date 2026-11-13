# Design — printer-desktop-enhancement

## Context
改造前（原 TinyPrintAssistant，现 PipPrinter）形态：托盘驻留 + 空 Home 窗体 + `WebsockServerService` 通过反射调 `PrintServices` 方法。
指令协议：`{cmd, label, printer, copies, fit, scale, paperKind, printPages}`，响应 `Result{code,timestamp,data,error,operator,uuid}`。
本次在保留原 WebSocket 反射分派协议的前提下，扩展命令与面板，升级到 .NET 10 LTS。

## Goals / Non-Goals
- Goals：可用托盘右键驱动所有功能；六页面板；队列/打印机/磁盘清理/更多文件类型/内嵌 Web 文档；.NET 10 + 最新稳定依赖
- Non-Goals：见 proposal

## Decisions

### D1 升级 .NET 10 LTS
- `TargetFramework` 改为 `net10.0-windows`，保留 `UseWindowsForms=true`
- `Fleck` 维持 1.2.0（已是该库最新稳定）
- `iTextSharp` 5.5.13.6（保留；SumatraPDF 分流路径不直接调用 iTextSharp，仅用于兼容未来可能扩展的 PDF 内容解析）
- `log4net` 维持 2.x 系（2.0.17 或最新 2.0.x）；`Microsoft.Extensions.Logging.Log4Net.AspNetCore` 升到 `10.0.0`（与 .NET 10 对齐）
- `Newtonsoft.Json` 升到 `13.0.4`（最新稳定；14 仍是 beta）
- 风险：`Application.SetHighDpiMode` 在 .NET 9+ 仍可用但标 `Obsolete`；改 `ApplicationConfiguration.Initialize()` 会丢失显式 DPI 模式，保留旧调用并加 `#pragma warning disable` 或 `IgnoreObsoleteWarning`

### D2 托盘右键菜单（ContextMenuStrip）
- 顶层 8 项：打开主窗口 / 快速打印… / 打印队列 / 打印机列表 / 磁盘清理 / 打开文档站点 / 设置 / 退出
- 「快速打印」弹文件选择器，走 `ExtendedPrintService.PrintLocal`
- 每项可独立走命令分派，无需打开主窗体
- 托盘双键事件保持现有行为（显示/隐藏主窗体）

### D3 TabControl 六页
- 打印（Print）：文件/URL 输入 + 打印机选择 + 份数 + 缩放 + 纸张 + 立即打印 / 加入队列
- 队列（Queue）：ListView 列 [JobId, 文件名, 打印机, 份数, 状态, 时间]，支持 删除/重试/清空
- 打印机（Printers）：DataGridView 列 [名称, 驱动, 默认, 状态]，支持 设为默认/测试页
- 磁盘（Disk）：ListView 大文件/临时文件，支持 清理/扫描/导出
- 设置（Settings）：默认打印机、清理白名单、文档服务端口、开机自启、日志级别
- 日志（Logs）：Readonly TextBox + 尾部滚动，显示 log4net 文件内容

### D4 打印队列模型
- `PrintQueueService` 单例，内存 `ConcurrentQueue<PrintJob>` + JSON 文件持久化 `data/queue.json`
- 每个 `PrintJob` 字段：`id, source(file|url|template), target, printer, copies, fit, scale, paperKind, pages, status(pending|printing|done|failed), enqueuedAt, startedAt, finishedAt, error`
- 顺序执行：后台 `Task` 消费队列，`PrinterService` 锁住目标打印机执行 SumatraPDF
- 失败重试：UI 点「重试」把状态改回 `pending` 并前置

### D5 更多文件类型
- `ExtendedPrintService.PrintLocal` 按扩展名分流：
  - `pdf` → SumatraPDF（现有）
  - `doc/docx/xls/xlsx/ppt/pptx` → 走 `Process.Start` 调系统默认 Office/WPS + 打印参数（若安装失败则提示用户）
  - `png/jpg/jpeg` → 调 `rundll32.exe shell32.dll,PrintFileW` 或 `mspaint` 兜底
  - `html/htm` → 调 `mshtml` 或 `chrome --headless --print-to-pdf` 转 PDF 再走 SumatraPDF
  - `txt` → 调 `notepad` 兜底
- 每个分流都走队列，统一状态
- 失败时回退到「打开文件供用户手动打印」

### D6 内嵌 Web 文档服务
- 用 Kestrel 起静态文件 + 3 个 REST 端点（`GET /api/status`, `GET /api/printers`, `POST /api/print` 走队列）
- 端口：默认 19861（与 WS 19860 相邻；可配置）
- 文档页：`docs/web/index.html`（总览）/`api.html`（REST）/`ws.html`（WebSocket 协议）/`styles.css`
- 启动时机：`Program.Main` 内 `StartWebServer`，随托盘一起驻留

### D7 设置存储
- `SettingsStore` 读/写 `config/appsettings.json`（`defaultPrinter, webPort, wsPort, cleanWhitelist, logLevel`）
- 开机自启用 `BaseServices.StartWhenSystemStart()`（保留）

### D8 日志
- 保留 log4net 2.x 配置；`Views/Home` 的日志页用 `FileStream` 顺序读 `log/debug/err/...` 最新文件
- `Tools.Logger` 保留

### D9 产品命名
- 英文名 **PipPrinter**，中文名 **小莱打印助手**，统一由 `Utils/Brand.cs` 提供常量
- `AssemblyName` / `RootNamespace` / csproj / sln / 可执行文件统一为 `PipPrinter`（`PipPrinter.exe`）
- 窗体标题、托盘提示、气泡、测试页、Web 文档站、`/api/status` 的 `app` / `appCn` 全部取 `Brand` 常量
- 保留内部代码命名空间 `DuoAssistant.*`（历史命名，与产品品牌解耦，避免大规模机械改名引入回归）

### D10 默认视图与托盘交互
- 主窗体启动时 `TabControl.SelectedIndex = 1`，**默认展示「打印队列」页**
- 托盘图标**双击显示主窗口**（`ShowMain(-1)`，不再切换隐藏）；最小化或关闭主窗口才收回托盘
- 托盘菜单「打开主窗口」同样保留当前页签，首次打开即为打印队列

### D11 托盘驻留的显示控制
- `Application.Run(Form)` 会强制 `Show()` 主窗体，覆盖构造函数里的 `Hide()`，实测启动时主窗口会意外弹出
- 改用 `Application.Run()`（无参消息循环）+ 静态 `Home` 引用保持窗体/NotifyIcon 存活
- `Home.SetVisibleCore` 加 `_allowVisible` 闸门，只有 `ShowMain()` 或 `--show` 才放行，防止框架自动显示
- 不再动态切换 `ShowInTaskbar`（运行时切换会重建句柄导致托盘/显示异常）
- 新增 `--show` 启动参数，便于首次运行与 UI 验收

## Alternatives
- 用 `System.Text.Json` 替 `Newtonsoft`：放弃。反射分派与现有 `JObject` 代码深度耦合，换 JSON 库成本高、收益低。
- 用 `WPF`/`MAUI` 重写 UI：放弃。Non-goal。
- 用 `HttpClient` 替 Fleck：放弃。Fleck 是 WS 服务端，HttpClient 是客户端。
- 用 `Microsoft.Extensions.Logging` 全量替换 log4net：放弃。保留 log4net 便于现有 appender 配置；桥接包只升大版本。
- 用 `Process.Start("cmd /c start ...")` 打印 Office：放弃。Office COM 打印走 `Process.Start(fileName, "/q /p")` 更稳，且避免 Shell 注入。

## Consequences
- .NET 10 下 iTextSharp 仍走 .NET Framework 兼容层，发布时需 `runtime` 一致；`iTextSharp` 仅被保留作为 API 扩展预留，本次未直接使用，可在后续 change 中移除
- 队列引入 JSON 持久化，首次运行需要创建 `data/` 目录；崩溃恢复需读 `queue.json` 把未完成 job 重置为 pending
- 文档服务起 Kestrel，占用 19861；与本机其他服务冲突需在设置里可改
- 托盘菜单 8 项 + 六页 Tab，UI 代码量约 600–900 行，需拆分到 `Views/Pages/*.cs` 保持可维护

## Verification
- 编译：`dotnet build -c Release` 通过，0 error
- 启动：`dotnet run` 后托盘可见，右键菜单 8 项可用
- 队列：加 2 个 PDF 到队列，观察顺序打印与状态流转
- 打印机：设置默认、测试页
- 磁盘：扫 `%TEMP%`，删 1 个文件
- Web：浏览器开 `http://127.0.0.1:19861`，`/api/printers` 返回 JSON
- 命令：用 WS 客户端发 `PrintFile`、`QueuePrint`、`GetQueue`、`CleanDisk`，返回 `Result` 结构

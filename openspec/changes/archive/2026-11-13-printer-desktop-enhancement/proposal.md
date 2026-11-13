# Change: printer-desktop-enhancement

## Why
改造前（原 TinyPrintAssistant）只有「托盘 + 空 Home 窗体 + WebSocket 反射分派 + 单文件 PDF 打印」。
缺乏用户直接操作入口（无右键菜单、无面板），无打印队列，无本地打印机管理，无磁盘清理，
不支持多种文件类型打印，无使用/开发文档可查。依赖也未随 .NET LTS 演进（仍停在 net8.0）。

## What Changes
- **产品命名**：英文名 **PipPrinter**，中文名 **小莱打印助手**；程序集、项目、解决方案与可执行文件统一为 `PipPrinter`。
- **默认视图与托盘交互**：主窗口默认展示「打印队列」页；双击托盘图标显示主窗口。
- **升级 .NET**：TargetFramework `net8.0-windows` → `net10.0-windows`（最新 LTS），并同步升级 NuGet 到最新稳定版。
- **托盘右键 UI/UX**：NotifyIcon 加 ContextMenuStrip，含 打开主窗口 / 快速打印 / 查看打印队列 / 打印机列表 / 磁盘清理 / 打开文档 / 设置 / 退出。
- **TabControl 多页面板**：打印 / 队列 / 打印机 / 磁盘 / 设置 / 日志 六个子页，可双击托盘切换显示。
- **打印队列**：基于内存 + JSON 持久化的 PrintQueueService，支持入队、顺序打印、失败重试、状态查询。
- **本地打印机列表**：PrinterService 列出发卡打印机、当前默认、可设为默认，附带驱动信息。
- **磁盘清理**：DiskCleanerService 支持清理 Windows 临时、应用缓存、大文件扫描与删除，带白名单。
- **更多打印文件类型**：ExtendedPrintService 支持 PDF / DOC / DOCX / XLS / XLSX / PPT / PPTX / PNG / JPG / HTML / TXT，经 SumatraPDF + 系统关联程序分流。
- **内嵌 Web 文档服务**：Kestrel（ASP.NET Core 静态托管）挂到 19861 端口，提供开发/使用文档页与 WebSocket 协议说明。
- **WebSocket 命令扩展**：`PrintFile`、`QueuePrint`、`GetQueue`、`CleanDisk`、`GetDiskInfo`、`GetPrinterList`（已存在，扩展字段）等。

## Impact
- Affected specs: print-file, print-queue, printer-list, disk-cleanup, web-docs, ui-tray
- Affected code:
  - `PipPrinter.csproj`（原 `TinyPrintAssistant.csproj`；TFM + 包升级 + 品牌元数据）
  - `Program.cs`（初始化顺序、Web 服务启动）
  - `Services/PrintServices.cs`（拆分为多个服务；保留反射分派兼容）
  - `Services/` 新增 `PrintQueueService`、`PrinterService`、`DiskCleanerService`、`ExtendedPrintService`、`WebServerService`、`SettingsStore`
  - `Views/Home.cs` / `Home.Designer.cs` / `Home.resx`（托盘菜单 + TabControl 面板）
  - `docs/web/`（静态文档站点）
  - `config/appsettings.json`（默认端口、清理策略、白名单）

## Non-goals
- 不做跨进程共享/远程打印队列同步（队列仍为单机）
- 不引入 WPF/MAUI，沿用 WinForms
- 不改动 log4net 2.x 日志格式与 appender 配置
- 不做 UAC 提权或系统服务化（开机自启仍走 HKLM Run，保持现状）
- 不实现 iTextSharp 的 PDF 内容解析/编辑能力（仅依赖 SumatraPDF 走打印驱动）

## Risks
- .NET 10 上 WinForms 与部分老 API 差异（如 `Application.SetHighDpiMode` 已弃用但可用）→ 用 `ApplicationConfiguration.Initialize()` 或保留旧 API + 兼容层
- Fleck 1.2.0 是最后一版稳定，无更新替代；升级到 .NET 10 后需回归验证 WS 连接
- iTextSharp 5.5.13.6 仍为 .NET Framework 兼容包，.NET 10 下保留 NU1701 警告（可接受；仅当需要 iTextSharp API 时才触发，SumatraPDF 分流路径不依赖它）
- 磁盘清理白名单缺失可能误删 → 默认只清理 `%TEMP%`、`%LOCALAPPDATA%\Temp`、本应用 `temp/`，其它目录需显式勾选

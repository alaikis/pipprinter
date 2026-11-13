# Tasks — printer-desktop-enhancement

> 状态：全部实现完成，验收记录见 `docs/superpowers/verify/printer-desktop-enhancement.md`。

## 1. 依赖与 TFM 升级
- [x] 1.1 `PipPrinter.csproj`：`TargetFramework` → `net10.0-windows`（最新 LTS）
- [x] 1.2 `iTextSharp` → `5.5.13.6`
- [x] 1.3 日志桥接处理：**移除** `Microsoft.Extensions.Logging.Log4Net.AspNetCore`（项目从未使用；其 10.0.0 强制依赖 log4net ≥ 3.3.0，8.0.0 会把 ASP.NET Core 8 程序集带入 net10 应用）
- [x] 1.4 `Newtonsoft.Json` → `13.0.4`
- [x] 1.5 `Fleck` 保持 `1.2.0`（已是最新稳定）
- [x] 1.6 `log4net` 保持 `2.0.17`（v3 改命名空间，非本次目标）
- [x] 1.7 `dotnet restore` 通过
- [x] 1.8 `dotnet build -c Release` 通过，**0 warning / 0 error**
- [x] 1.9 新增 `FrameworkReference Microsoft.AspNetCore.App` 供 Kestrel 使用

## 2. 服务层
- [x] 2.1 新增 `Models/PrintJob.cs`：任务模型 + `JobStatus` 常量
- [x] 2.2 新增 `Services/PrintQueueService.cs`：顺序消费、JSON 持久化 `data/queue.json`、失败重试、崩溃恢复、变更事件
- [x] 2.3 新增 `Services/PrinterService.cs`：枚举/默认切换/测试页，附驱动、端口、纸张规格
- [x] 2.4 新增 `Services/DiskCleanerService.cs`：白名单、允许根目录二次校验、试运行、回收站删除
- [x] 2.5 新增 `Services/ExtendedPrintService.cs`：23 种扩展名分流（SumatraPDF / Shell print / Edge-Chrome headless）
- [x] 2.6 新增 `Services/WebServerService.cs`：Kestrel 静态文档站 + 10 个 REST 端点
- [x] 2.7 新增 `Services/SettingsStore.cs`：读写 `config/appsettings.json`
- [x] 2.8 重构 `Services/PrintServices.cs`：保留 5 个旧命令，新增 15 个命令（共 20 个）
- [x] 2.9 新增 `Services/CommandDispatcher.cs`：WS / REST / UI 共用单一分派入口 + 命令白名单
- [x] 2.10 新增 `Utils/Brand.cs`：英文名 PipPrinter / 中文名 小莱打印助手 统一常量

## 3. UI/UX
- [x] 3.1 `Views/Home.resx` 精简为空资源（原强类型 Icon 引用 System.Drawing.Common v5，在 .NET 10 反序列化会失败）；托盘图标改为运行时加载 `Resource/bitbug_favicon.ico`
- [x] 3.2 `Views/Home.cs` + `Home.Designer.cs`：托盘 NotifyIcon 加 ContextMenuStrip（8 项）
- [x] 3.3 新增 `Views/Pages/PrintTab.cs`：文件/URL + 打印机 + 份数/缩放/方向/纸张/页码 + 立即打印/入队/测试页
- [x] 3.4 新增 `Views/Pages/QueueTab.cs`：ListView + 删除/重试/取消/清空 + 2s 自动刷新
- [x] 3.5 新增 `Views/Pages/PrintersTab.cs`：列表 + 设为默认/测试页/打开文档
- [x] 3.6 新增 `Views/Pages/DiskTab.cs`：扫描/勾选/试运行/确认清理/导出清单 + 磁盘容量
- [x] 3.7 新增 `Views/Pages/SettingsTab.cs`：端口/默认打印机/清理策略/开机自启/白名单
- [x] 3.8 新增 `Views/Pages/LogsTab.cs`：只读日志查看 + 自动刷新
- [x] 3.9 `Home` 加 TabControl 装 6 页，最小化/关闭收进托盘，仅「退出」结束进程
- [x] 3.10 **默认打开「打印队列」页**（`SelectedIndex = 1`）
- [x] 3.11 **双击托盘图标显示主窗口**（不再切换隐藏）
- [x] 3.12 WinForms 跟随系统视觉样式；文档站另支持 `prefers-color-scheme` 深色模式
- [x] 3.13 新增 `Views/Pages/Ui.cs` 统一控件样式与跨线程封送 `Safe()`

## 4. Web 文档
- [x] 4.1 新增 `docs/web/index.html`（总览 + 快速开始 + 架构）
- [x] 4.2 新增 `docs/web/api.html`（REST + curl 示例 + 错误码）
- [x] 4.3 新增 `docs/web/ws.html`（WebSocket 协议 + 20 条命令表 + 请求/响应 JSON）
- [x] 4.4 新增 `docs/web/dev.html`（构建、目录结构、/impetus 规范、新增命令流程、配置与依赖矩阵）
- [x] 4.5 新增 `docs/web/styles.css`
- [x] 4.6 `PipPrinter.csproj` 复制 `docs/web/*` 与 `config/appsettings.json` 到输出目录
- [x] 4.7 `Program.cs` 启动 `WebServerService`（随托盘驻留）

## 5. 配置与健壮性
- [x] 5.1 新增 `config/appsettings.json`（端口、命令白名单、清理策略）
- [x] 5.2 `SettingsStore` 默认值兜底 + 原子写入
- [x] 5.3 `BaseServices` 开机自启改 HKCU（无需管理员），修正 exe 路径，新增 `DisableAutoStart` / `IsAutoStartEnabled`
- [x] 5.4 单实例 Mutex，避免 WS/Web 端口冲突
- [x] 5.5 修复 log4net 仓储不匹配导致分级日志始终为空的问题；`log4net.config` 根级别改为单个 `ALL`
- [x] 5.6 重写 `Utils/DriverCheck.cs`：优先随包资源恢复 SumatraPDF，联网下载仅作兜底；`async void` 改为同步 `EnsureDriver()`

## 6. 验证
- [x] 6.1 `dotnet build -c Release` 通过（0 warning / 0 error）
- [x] 6.2 应用启动驻留托盘，右键菜单 8 项与双击行为按代码实现；输出目录资产齐全
- [x] 6.3 队列：入队不存在的文件，状态 pending → printing → failed，错误原因正确
- [x] 6.4 打印机：`/api/printers` 返回本机 4 台打印机及驱动/端口/默认标记
- [x] 6.5 磁盘：扫描返回 3 块磁盘；`CleanDisk` 默认 dryRun 且受保护路径全部跳过（Deleted=0）
- [x] 6.6 Web：`http://127.0.0.1:19861` 返回首页，`/api/status` 返回 .NET 10.0.8 / 23 种类型 / 队列摘要
- [x] 6.7 WS：收到 `startPrint` 欢迎、`GetStatus` 响应、`QueueChanged` 主动推送、`QueuePrint` 响应
- [x] 6.8 崩溃恢复：手工把 `queue.json` 中任务置为 printing，重启后重置为 pending
- [x] 6.9 安全：非白名单命令返回 403，非法 JSON 返回 400
- [x] 6.10 `/impetus-verify` 验收报告归档

## 7. 收尾
- [x] 7.1 `.impetus.yaml` → `phase: archive`
- [x] 7.2 change 归档到 `openspec/changes/archive/`
- [x] 7.3 本地 `git commit`，中文提交信息
- [ ] 7.4 `git push` 到 Codeup —— **未执行**：推送为外部写操作，等待用户确认后执行

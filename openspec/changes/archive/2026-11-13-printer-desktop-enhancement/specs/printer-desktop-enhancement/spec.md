# Delta Spec — printer-desktop-enhancement

## ADDED Requirements

### Requirement: 托盘右键菜单
NotifyIcon 提供 ContextMenuStrip，含 8 项：打开主窗口、快速打印…、打印队列、打印机列表、磁盘清理、打开文档站点、设置、退出。

#### Scenario: 用户右键托盘图标
- Given 应用已驻留托盘
- When 用户在托盘图标上点右键
- Then 弹出 ContextMenuStrip，8 项全部可用，且无需打开主窗体即可触发命令

#### Scenario: 快速打印
- Given 用户在托盘右键菜单点「快速打印…」
- When 用户在文件选择器选一个 PDF
- Then 该文件进入打印队列并开始打印

### Requirement: 打印队列
PrintQueueService 提供内存 + JSON 持久化的顺序队列；每个 PrintJob 含 `id, source, target, printer, copies, fit, scale, paperKind, pages, status(pending|printing|done|failed), enqueuedAt, startedAt, finishedAt, error`。

#### Scenario: 顺序打印
- Given 队列有 2 个 PDF 任务
- When 后台 Task 消费
- Then 第一个完成后立即开始第二个，状态依次 pending→printing→done

#### Scenario: 失败重试
- Given 队列第 2 个任务 failed（打印机离线）
- When 用户点「重试」
- Then 该任务 status 改回 pending 并前置到队列头

#### Scenario: 崩溃恢复
- Given 应用被强杀，`data/queue.json` 里有 1 个 printing 状态 job
- When 应用重启
- Then 该 job status 重置为 pending，等待重新消费

### Requirement: 本地打印机列表
PrinterService 提供 `GetInstalled/GetDefault/SetDefault/PrintTestPage`，UI 用 DataGridView 展示。

#### Scenario: 设为默认
- Given 当前默认是「Microsoft Print to PDF」
- When 用户在打印机页选中「HP LaserJet 400」点「设为默认」
- Then 系统默认打印机切换为 HP，UI 标记更新

#### Scenario: 测试页
- Given 选中打印机在线
- When 用户点「测试页」
- Then 打印机出 1 页测试页，UI 提示成功

### Requirement: 磁盘清理
DiskCleanerService 支持 扫描 %TEMP%/应用缓存/大文件，删除时走白名单保护。

#### Scenario: 扫描临时文件
- Given `%TEMP%` 有 300MB 旧文件
- When 用户在磁盘页点「扫描」
- Then ListView 显示各文件路径/大小/最后修改时间，可多选删除

#### Scenario: 白名单保护
- Given 白名单含 `Program Files`、`Windows`
- When 清理扫描到这些目录
- Then 直接跳过，不纳入结果

### Requirement: 多类型文件打印
ExtendedPrintService 按扩展名分流：pdf→SumatraPDF；doc/docx/xls/xlsx/ppt/pptx→Office COM；png/jpg→shell；html/htm→chrome headless 转 PDF；txt→notepad。

#### Scenario: 打印 Word
- Given 用户拖入 `report.docx` 到打印页
- When 点「打印」
- Then 系统默认 Office/WPS 接管打印，状态流转为 done

#### Scenario: 打印图片
- Given 用户拖入 `photo.png`
- When 点「打印」
- Then 调 `rundll32 shell32 PrintFileW`，状态 done

### Requirement: 内嵌 Web 文档服务
WebServerService 用 Kestrel 起静态文档站 + REST 端点，默认 19861，可配置。

#### Scenario: 浏览器查看文档
- Given 应用运行中
- When 用户打开 `http://127.0.0.1:19861`
- Then 显示总览/快速开始/REST 文档/WS 协议四个页面

#### Scenario: REST 查询打印机
- Given 应用运行中
- When 客户端 `GET /api/printers`
- Then 返回 `{code:200, data:[{name, isDefault, driver, status}]}`

### Requirement: 产品命名
应用对外统一使用英文名 `PipPrinter`、中文名 `小莱打印助手`；程序集、项目、解决方案与可执行文件名为 `PipPrinter`。

#### Scenario: 用户可见名称一致
- Given 应用已安装
- When 用户查看开始菜单、任务管理器、托盘提示、窗体标题或 Web 文档站
- Then 显示 PipPrinter / 小莱打印助手，不再出现 TinyPrintAssistant / TintAssistant

#### Scenario: 状态接口返回双名称
- Given 应用运行中
- When 客户端请求 `GET /api/status`
- Then `data.app` 为 `PipPrinter`，`data.appCn` 为 `小莱打印助手`

### Requirement: 默认视图与托盘双击
主窗口默认展示「打印队列」页；双击托盘图标显示主窗口。

#### Scenario: 首次打开主窗口
- Given 应用刚启动并驻留托盘
- When 用户双击托盘图标或点菜单「打开主窗口」
- Then 主窗口显示且选中「打印队列」页签

#### Scenario: 双击不隐藏
- Given 主窗口当前可见
- When 用户再次双击托盘图标
- Then 主窗口保持可见并被置前，不会被隐藏

#### Scenario: 启动时不弹窗
- Given 用户双击 `PipPrinter.exe`（不带参数）
- When 应用启动完成
- Then 主窗口不可见，仅托盘图标 + 气泡提示出现；WebSocket 与 Web 文档服务均已就绪

#### Scenario: 验收用直接显示
- Given 用户以 `PipPrinter.exe --show` 启动
- When 应用启动完成
- Then 主窗口直接显示并选中「打印队列」页

#### Scenario: 收回托盘
- Given 主窗口可见
- When 用户最小化或点击关闭按钮
- Then 窗口隐藏到托盘，进程不退出；只有菜单「退出」才结束进程

### Requirement: 升级 .NET 10 LTS
PipPrinter 目标框架 `net10.0-windows`，依赖升到最新稳定版。

#### Scenario: 编译
- Given .NET 10.0.300 SDK 已安装
- When 运行 `dotnet build -c Release`
- Then 0 error，允许 warning（iTextSharp NU1701）

## MODIFIED Requirements

### Requirement: WebSocket 命令扩展
保留旧 `PrintPdf/GetPrinterList/GetPdfDriverConfig/SetPdfPrintDriverConfig/TemplatePrint`，新增 `PrintFile/QueuePrint/GetQueue/CleanDisk/GetDiskInfo`。

#### Scenario: PrintFile 命令
- Given WS 客户端发 `{"cmd":"PrintFile","path":"C:\\x\\a.pdf","printer":"HP","copies":2}`
- When 服务端分派
- Then 走 ExtendedPrintService，返回 `Result{code:200, operator:"PrintFile"}`

#### Scenario: QueuePrint 命令
- Given 客户端发 `{"cmd":"QueuePrint","path":"C:\\x\\a.docx"}`
- When 服务端分派
- Then 入 PrintQueueService，返回 job id

### Requirement: 统一 Result 响应
保留 `Result{code, timestamp, data, error, operator, uuid}` 结构，`timestamp` 用 Unix 秒（UTC）。

#### Scenario: 错误返回
- Given 命令失败（打印机不存在）
- When 服务端捕获
- Then 返回 `Result{code:400, error:"printer not found", operator:"PrintFile"}`

## REMOVED Requirements
（本次无删除需求；旧命令全部保留）
